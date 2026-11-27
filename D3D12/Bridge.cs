using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Threading;

using Dalamud.Utility;

using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;

using static TerraFX.Interop.DirectX.D3D_FEATURE_LEVEL;
using static TerraFX.Interop.DirectX.D3D11_BIND_FLAG;
using static TerraFX.Interop.DirectX.D3D12_COMMAND_LIST_TYPE;
using static TerraFX.Interop.DirectX.D3D12_FENCE_FLAGS;
using static TerraFX.Interop.DirectX.D3D12_HEAP_FLAGS;
using static TerraFX.Interop.DirectX.D3D12_HEAP_TYPE;
using static TerraFX.Interop.DirectX.D3D12_QUERY_HEAP_TYPE;
using static TerraFX.Interop.DirectX.D3D12_QUERY_TYPE;
using static TerraFX.Interop.DirectX.D3D12_RESOURCE_STATES;
using static TerraFX.Interop.DirectX.DirectX;
using static TerraFX.Interop.DirectX.DXGI;
using static TerraFX.Interop.DirectX.DXGI_FORMAT;
using static TerraFX.Interop.Windows.Windows;

namespace UpscaleBuddy.D3D12;

/// <summary>
/// A D3D11 texture opened on D3D12. It is our own twin, or the game's texture itself when that texture is shared.
/// </summary>
public sealed unsafe class Twin(ID3D11Texture2D* texture11, Input input)
{
	public readonly ID3D11Texture2D* Texture11 = texture11;
	public readonly Input Input = input;
}

/// <summary>
/// Records D3D12 work into the open command list.
/// Every texture arrives in COMMON state, and the delegate must leave it in that state.
/// state holds everything the delegate needs, so a static lambda can record without an allocation.
/// </summary>
public unsafe delegate void RecordDelegate<TState>(ID3D12GraphicsCommandList* list, in TState state);

public enum QueueKind
{
	Direct,
	Compute,
}

/// <summary>
/// A D3D12 device next to the game's D3D11 device, for the D3D12 libraries (upscalers, frame generation).
/// The bridge opens game textures directly when the game created them NT shared (Game/SharedTargets).
/// It copies other textures into shared twins.
/// It copies depth through a shader into R32_FLOAT, because the libraries take no 24 bit depth.
/// A shared fence orders both queues.
/// </summary>
public unsafe class Bridge: IDisposable
{
	private const int Frames = 8; // Above the swapchain's frame latency, so Present throttles rather than the allocators
	private const int DirectPruneFrames = 120;

	private readonly ID3D11Device5* device11;
	private readonly ID3D12Device* device12;
	private readonly ID3D12CommandQueue* queue;
	private readonly ID3D12Fence* fence12;
	private readonly ID3D11Fence* fence11;
	private readonly ID3D12Fence* inputFence12;
	private readonly ID3D11Fence* inputFence11;
	private readonly ID3D12Fence* outputFence;

	// Execute uses allocators 0 to Frames - 1. SubmitAsync uses allocators Frames to 2 Frames - 1.
	private readonly ID3D12CommandAllocator*[] allocators = new ID3D12CommandAllocator*[Frames * 2];
	private readonly ID3D12GraphicsCommandList*[] lists = new ID3D12GraphicsCommandList*[Frames * 2];
	private readonly ulong[] frameFence = new ulong[Frames * 2];
	private readonly ID3D11ComputeShader* depthShader;
	private readonly AutoResetEvent outputEvent = new(false);

	// Each allocator has two D3D12 timestamps around its recorded work.
	private readonly ID3D12QueryHeap* queryHeap;
	private readonly ID3D12Resource* readback;
	private readonly ulong* timestamps;
	private readonly ulong timestampFrequency;
	private readonly bool[] measured = new bool[Frames * 2];
	private ulong fenceValue;
	private ulong inputValue;
	private ulong outputValue;
	private int frame;
	private int asyncFrame;

	private ID3D11DeviceContext4* context4;
	private ID3D11DeviceContext* context4Source;

	// Depth copy source view, by texture
	private ID3D11Texture2D* depthSource;
	private ID3D11ShaderResourceView* depthSourceView;
	private ID3D11UnorderedAccessView* depthUav;
	private ID3D11Texture2D* depthUavTexture;

	// Game textures by pointer, null when not shareable.
	// Each entry holds a reference, so a reallocation can't reuse the address while the entry is cached.
	// The bridge releases unused entries.
	private readonly Dictionary<nint, Twin?> direct = [];
	private readonly Dictionary<nint, int> directSeen = [];
	private int executeCount;

	/// <summary>The number of D3D11 waits on a D3D12 fence, for all bridges.</summary>
	public static long D3D11Waits;

	public Bridge(ID3D11Device* device, QueueKind queueKind = QueueKind.Direct)
	{
		D3D12_COMMAND_LIST_TYPE listType = queueKind == QueueKind.Compute ? D3D12_COMMAND_LIST_TYPE_COMPUTE : D3D12_COMMAND_LIST_TYPE_DIRECT;
		try
		{
			this.device11 = Dx.Query<ID3D11Device5>(device);

			// Same adapter as the game
			IDXGIDevice* dxgiDevice = Dx.Query<IDXGIDevice>(device);
			IDXGIAdapter* adapter;
			HRESULT adapterResult = dxgiDevice->GetAdapter(&adapter);
			dxgiDevice->Release();
			ThrowIfFailed(adapterResult);
			ID3D12Device* createdDevice;
			HRESULT deviceResult = D3D12CreateDevice((IUnknown*)adapter, D3D_FEATURE_LEVEL_11_0, __uuidof<ID3D12Device>(), (void**)&createdDevice);
			if (deviceResult.FAILED && Util.IsWine())
				deviceResult = WineVkd3d.CreateDevice((IUnknown*)adapter, &createdDevice);

			adapter->Release();
			ThrowIfFailed(deviceResult);
			this.device12 = createdDevice;

			D3D12_COMMAND_QUEUE_DESC queueDesc = new() { Type = listType };
			ID3D12CommandQueue* createdQueue;
			ThrowIfFailed(this.device12->CreateCommandQueue(&queueDesc, __uuidof<ID3D12CommandQueue>(), (void**)&createdQueue));
			this.queue = createdQueue;

			this.fence12 = this.CreateFence(true);
			this.fence11 = this.OpenOnD3D11(this.fence12);
			this.inputFence12 = this.CreateFence(true);
			this.inputFence11 = this.OpenOnD3D11(this.inputFence12);
			this.outputFence = this.CreateFence(false);

			for (int i = 0; i < Frames * 2; i++)
			{
				ID3D12CommandAllocator* allocator;
				ThrowIfFailed(this.device12->CreateCommandAllocator(listType, __uuidof<ID3D12CommandAllocator>(), (void**)&allocator));
				this.allocators[i] = allocator;
				ID3D12GraphicsCommandList* list;
				ThrowIfFailed(this.device12->CreateCommandList(0, listType, allocator, null, __uuidof<ID3D12GraphicsCommandList>(), (void**)&list));
				this.lists[i] = list;
				ThrowIfFailed(list->Close());
			}

			D3D12_QUERY_HEAP_DESC heapDesc = new() { Type = D3D12_QUERY_HEAP_TYPE_TIMESTAMP, Count = Frames * 4 };
			ID3D12QueryHeap* createdHeap;
			ThrowIfFailed(this.device12->CreateQueryHeap(&heapDesc, __uuidof<ID3D12QueryHeap>(), (void**)&createdHeap));
			this.queryHeap = createdHeap;

			D3D12_HEAP_PROPERTIES readbackHeap = new(D3D12_HEAP_TYPE_READBACK);
			D3D12_RESOURCE_DESC readbackDesc = D3D12_RESOURCE_DESC.Buffer(Frames * 4 * sizeof(ulong));
			ID3D12Resource* createdReadback;
			ThrowIfFailed(this.device12->CreateCommittedResource(&readbackHeap, D3D12_HEAP_FLAG_NONE, &readbackDesc, D3D12_RESOURCE_STATE_COPY_DEST,
				null, __uuidof<ID3D12Resource>(), (void**)&createdReadback));
			this.readback = createdReadback;
			void* mapped; // A persistent map is fine for readback heaps
			ThrowIfFailed(this.readback->Map(0, null, &mapped));
			this.timestamps = (ulong*)mapped;
			ulong frequency;
			ThrowIfFailed(this.queue->GetTimestampFrequency(&frequency));
			this.timestampFrequency = frequency;

			this.depthShader = Dx.CreateComputeShader(device, "Shaders.depth_copy.cso");
		}
		catch
		{
			this.Dispose();
			throw;
		}
	}

	public ID3D12Device* Device12 => this.device12;

	public ID3D12CommandQueue* Queue => this.queue;

	/// <summary>GPU time of the recorded work, averaged, when measured</summary>
	public double RecordedMs { get; private set; }

	/// <summary>CPU time waiting for a free allocator, averaged, when measured</summary>
	public double CpuStallMs { get; private set; }

	/// <summary>
	/// Hands the D3D11 work so far to D3D12, runs what record records, and makes D3D11 wait for it.
	/// When record fails, Execute still completes the fence handoff, and D3D11 waits on it.
	/// Execute then rethrows the exception.
	/// </summary>
	public void Execute<TState>(ID3D11DeviceContext* context, in TState state, RecordDelegate<TState> record, bool measure)
	{
		ID3D11DeviceContext4* context4 = this.Context4(context);
		this.executeCount++;
		if (this.executeCount % DirectPruneFrames == 0)
			this.PruneDirect();

		ThrowIfFailed(context4->Signal(this.fence11, ++this.fenceValue));
		context->Flush();

		int slot = this.frame;
		this.frame = (this.frame + 1) % Frames;
		ExceptionDispatchInfo? failure = this.Record(slot, this.fence12, state, record, measure, out ID3D12GraphicsCommandList* list);

		ThrowIfFailed(this.queue->Wait(this.fence12, this.fenceValue));
		if (failure == null)
			this.queue->ExecuteCommandLists(1, (ID3D12CommandList**)&list);
		ThrowIfFailed(this.queue->Signal(this.fence12, ++this.fenceValue));
		this.frameFence[slot] = this.fenceValue;

		ThrowIfFailed(context4->Wait(this.fence11, this.fenceValue));
		D3D11Waits++;
		failure?.Throw();
	}

	/// <summary>Call on the render thread. Returns the input value for SubmitAsync.</summary>
	public ulong SignalInput(ID3D11DeviceContext* context)
	{
		ThrowIfFailed(this.Context4(context)->Signal(this.inputFence11, ++this.inputValue));
		context->Flush();
		return this.inputValue;
	}

	/// <summary>Call SubmitAsync one time for each reserved value, in the order of reservation.</summary>
	public ulong ReserveOutput() => Interlocked.Increment(ref this.outputValue);

	/// <summary>Call on the worker thread. This method signals output also when record throws.</summary>
	public void SubmitAsync<TState>(ulong input, ulong output, in TState state, RecordDelegate<TState> record, bool measure = false)
	{
		int slot = Frames + this.asyncFrame;
		this.asyncFrame = (this.asyncFrame + 1) % Frames;
		ExceptionDispatchInfo? failure = this.Record(slot, this.outputFence, state, record, measure, out ID3D12GraphicsCommandList* list);

		ThrowIfFailed(this.queue->Wait(this.inputFence12, input));
		if (failure == null)
			this.queue->ExecuteCommandLists(1, (ID3D12CommandList**)&list);
		ThrowIfFailed(this.queue->Signal(this.outputFence, output));
		this.frameFence[slot] = output;
		failure?.Throw();
	}

	/// <summary>A removed device reads every fence value as complete.</summary>
	public bool DeviceLost => this.device12->GetDeviceRemovedReason().FAILED;

	public bool Completed(ulong output) => this.outputFence->GetCompletedValue() >= output;

	/// <summary>Only one thread at a time can call this method.</summary>
	public bool WaitOutput(ulong output, uint milliseconds)
	{
		long deadline = Environment.TickCount64 + milliseconds;
		while (!this.Completed(output))
		{
			long remaining = deadline - Environment.TickCount64;
			if (remaining <= 0)
				return false;

			ThrowIfFailed(this.outputFence->SetEventOnCompletion(output, (HANDLE)this.outputEvent.SafeWaitHandle.DangerousGetHandle()));
			this.outputEvent.WaitOne((int)remaining);
		}

		return true;
	}

	/// <summary>This method closes the list also when record throws.</summary>
	private ExceptionDispatchInfo? Record<TState>(int slot, ID3D12Fence* fence, in TState state, RecordDelegate<TState> record, bool measure,
		out ID3D12GraphicsCommandList* list)
	{
		list = this.lists[slot];
		long waitStart = Stopwatch.GetTimestamp();
		CpuWait(fence, this.frameFence[slot]);
		if (measure)
			this.CpuStallMs = (this.CpuStallMs * 0.9) + (Stopwatch.GetElapsedTime(waitStart).TotalMilliseconds * 0.1);
		if (this.measured[slot])
		{
			ulong elapsed = this.timestamps[(slot * 2) + 1] - this.timestamps[slot * 2];
			this.RecordedMs = (this.RecordedMs * 0.9) + (elapsed * 1000.0 / this.timestampFrequency * 0.1);
			this.measured[slot] = false;
		}

		ThrowIfFailed(this.allocators[slot]->Reset());
		ThrowIfFailed(list->Reset(this.allocators[slot], null));
		if (measure)
			list->EndQuery(this.queryHeap, D3D12_QUERY_TYPE_TIMESTAMP, (uint)slot * 2);

		ExceptionDispatchInfo? failure = null;
		try
		{
			record(list, state);
			if (measure)
			{
				list->EndQuery(this.queryHeap, D3D12_QUERY_TYPE_TIMESTAMP, ((uint)slot * 2) + 1);
				list->ResolveQueryData(this.queryHeap, D3D12_QUERY_TYPE_TIMESTAMP, (uint)slot * 2, 2, this.readback, (ulong)slot * 2 * sizeof(ulong));
				this.measured[slot] = true;
			}
		}
		catch (Exception e)
		{
			failure = ExceptionDispatchInfo.Capture(e);
		}
		finally
		{
			ThrowIfFailed(list->Close());
		}

		return failure;
	}

	private ID3D11DeviceContext4* Context4(ID3D11DeviceContext* context)
	{
		if (context != this.context4Source)
		{
			Dx.Release(this.context4);
			this.context4 = Dx.Query<ID3D11DeviceContext4>(context);
			this.context4Source = context;
		}

		return this.context4;
	}

	private ID3D12Fence* CreateFence(bool shared)
	{
		ID3D12Fence* fence;
		ThrowIfFailed(this.device12->CreateFence(0, shared ? D3D12_FENCE_FLAG_SHARED : D3D12_FENCE_FLAG_NONE, __uuidof<ID3D12Fence>(), (void**)&fence));
		return fence;
	}

	private ID3D11Fence* OpenOnD3D11(ID3D12Fence* fence)
	{
		HANDLE handle;
		ThrowIfFailed(this.device12->CreateSharedHandle((ID3D12DeviceChild*)fence, null, GENERIC_ALL, null, &handle));
		ID3D11Fence* opened;
		HRESULT result = this.device11->OpenSharedFence(handle, __uuidof<ID3D11Fence>(), (void**)&opened);
		CloseHandle(handle);
		ThrowIfFailed(result);
		return opened;
	}

	/// <summary>Blocks until the D3D12 fence reaches the value</summary>
	private static void CpuWait(ID3D12Fence* fence, ulong value)
	{
		if (fence->GetCompletedValue() < value)
			ThrowIfFailed(fence->SetEventOnCompletion(value, HANDLE.NULL));
	}

	/// <summary>The game's texture opened on D3D12, null when the game didn't create it shared</summary>
	public Twin? Direct(ID3D11Texture2D* texture)
	{
		if (!this.direct.TryGetValue((nint)texture, out Twin? twin))
		{
			texture->AddRef();
			this.direct[(nint)texture] = null;
			D3D11_TEXTURE2D_DESC desc = Dx.GetDesc(texture);
			if ((desc.MiscFlags & Dx.MiscSharedNtHandle) == Dx.MiscSharedNtHandle && desc.MipLevels == 1)
			{
				twin = new Twin(texture, new Input(this.OpenOnD3D12(texture), desc.Format, desc.Width, desc.Height,
					(desc.BindFlags & (uint)D3D11_BIND_UNORDERED_ACCESS) != 0));
			}

			this.direct[(nint)texture] = twin;
		}

		this.directSeen[(nint)texture] = this.executeCount;
		return twin;
	}

	/// <summary>Drops game textures not seen lately. The D3D12 queue is long done with them.</summary>
	private void PruneDirect()
	{
		List<nint> stale = [];
		foreach ((nint texture, int seen) in this.directSeen)
		{
			if (this.executeCount - seen >= DirectPruneFrames)
				stale.Add(texture);
		}

		foreach (nint texture in stale)
			this.ReleaseDirect(texture);
	}

	private void ReleaseDirect(nint texture)
	{
		if (this.direct[texture] is { } twin)
			Dx.Release(twin.Input.Resource);
		Dx.Release((ID3D11Texture2D*)texture);
		this.direct.Remove(texture);
		this.directSeen.Remove(texture);
	}

	/// <summary>Our NT shared texture, opened on D3D12</summary>
	public Twin CreateTwin(DXGI_FORMAT format, uint width, uint height)
	{
		ID3D11Texture2D* texture = Dx.CreateTexture((ID3D11Device*)this.device11, width, height, format, 1, Dx.BindShaderResourceAndUav,
			misc: Dx.MiscSharedNtHandle);
		try
		{
			return new Twin(texture, new Input(this.OpenOnD3D12(texture), format, width, height, true));
		}
		catch
		{
			texture->Release();
			throw;
		}
	}

	/// <summary>Our twin at the size of source, in the format of source unless a format is given</summary>
	public Twin CreateTwinLike(ID3D11Texture2D* source, DXGI_FORMAT format = DXGI_FORMAT_UNKNOWN)
	{
		D3D11_TEXTURE2D_DESC desc = Dx.GetDesc(source);
		return this.CreateTwin(format == DXGI_FORMAT_UNKNOWN ? desc.Format : format, desc.Width, desc.Height);
	}

	public static void Release(Twin? twin)
	{
		if (twin == null)
			return;

		Dx.Release(twin.Input.Resource);
		Dx.Release(twin.Texture11);
	}

	private ID3D12Resource* OpenOnD3D12(ID3D11Texture2D* texture)
	{
		IDXGIResource1* shared = Dx.Query<IDXGIResource1>(texture);
		HANDLE handle;
		HRESULT handleResult = shared->CreateSharedHandle(null, DXGI_SHARED_RESOURCE_READ | DXGI_SHARED_RESOURCE_WRITE, null, &handle);
		shared->Release();
		ThrowIfFailed(handleResult);

		ID3D12Resource* resource;
		HRESULT openResult = this.device12->OpenSharedHandle(handle, __uuidof<ID3D12Resource>(), (void**)&resource);
		CloseHandle(handle);
		ThrowIfFailed(openResult);
		return resource;
	}

	/// <summary>Copies the top left of the source, as much as both textures hold.</summary>
	public static void Copy(ID3D11DeviceContext* context, ID3D11Texture2D* destination, ID3D11Texture2D* source, in Input twin)
	{
		D3D11_TEXTURE2D_DESC desc = Dx.GetDesc(source);
		D3D11_BOX box = new() { right = Math.Min(desc.Width, twin.Width), bottom = Math.Min(desc.Height, twin.Height), back = 1 };
		context->CopySubresourceRegion((ID3D11Resource*)destination, 0, 0, 0, 0, (ID3D11Resource*)source, 0, &box);
	}

	/// <summary>Copies the top left width x height of the game depth (24 bit) into an R32_FLOAT twin.</summary>
	public void CopyDepth(ID3D11DeviceContext* context, ID3D11Texture2D* source, Twin depth, uint width, uint height)
	{
		// The view keeps the texture alive, so a new depth texture can't get this address
		if (source != this.depthSource)
		{
			Dx.Release(this.depthSourceView);
			this.depthSourceView = null;
			this.depthSource = source;
			this.depthSourceView = Dx.CreateSrv((ID3D11Device*)this.device11, source, Dx.ReadableFormat(Dx.GetDesc(source).Format));
		}

		if (depth.Texture11 != this.depthUavTexture)
		{
			Dx.Release(this.depthUav);
			this.depthUav = Dx.CreateUav((ID3D11Device*)this.device11, depth.Texture11, DXGI_FORMAT_R32_FLOAT);
			this.depthUavTexture = depth.Texture11;
		}

		ID3D11ShaderResourceView* srv = this.depthSourceView;
		ID3D11UnorderedAccessView* uav = this.depthUav;
		ID3D11ShaderResourceView* noSrv = null;
		ID3D11UnorderedAccessView* noUav = null;
		context->CSSetShaderResources(0, 1, &srv);
		context->CSSetUnorderedAccessViews(0, 1, &uav, null);
		context->CSSetShader(this.depthShader, null, 0);
		context->Dispatch((width + 7) / 8, (height + 7) / 8, 1);
		context->CSSetUnorderedAccessViews(0, 1, &noUav, null);
		context->CSSetShaderResources(0, 1, &noSrv);
		context->CSSetShader(null, null, 0);
	}

	/// <summary>
	/// Waits until the queue is idle. Callers dispose their library contexts and twins after this call.
	/// </summary>
	public void WaitIdle()
	{
		if (this.queue != null && this.fence12 != null)
		{
			this.queue->Signal(this.fence12, ++this.fenceValue);
			CpuWait(this.fence12, this.fenceValue);
		}
	}

	public void Dispose()
	{
		this.WaitIdle();
		foreach (nint texture in new List<nint>(this.direct.Keys))
			this.ReleaseDirect(texture);

		foreach (ID3D12GraphicsCommandList* list in this.lists)
			Dx.Release(list);
		foreach (ID3D12CommandAllocator* allocator in this.allocators)
			Dx.Release(allocator);
		Dx.Release(this.readback);
		Dx.Release(this.queryHeap);
		Dx.Release(this.depthUav);
		Dx.Release(this.depthSourceView);
		Dx.Release(this.depthShader);
		Dx.Release(this.context4);
		Dx.Release(this.fence11);
		Dx.Release(this.fence12);
		Dx.Release(this.inputFence11);
		Dx.Release(this.inputFence12);
		Dx.Release(this.outputFence);
		this.outputEvent.Dispose();
		Dx.Release(this.queue);
		Dx.Release(this.device12);
		Dx.Release(this.device11);
		GC.SuppressFinalize(this);
	}
}
