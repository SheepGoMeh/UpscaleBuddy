using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.ExceptionServices;

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

/// <summary>A D3D11 texture opened on D3D12: our own twin, or the game's texture itself when it's shared</summary>
public sealed unsafe class Twin(ID3D11Texture2D* texture11, Input input)
{
	public readonly ID3D11Texture2D* Texture11 = texture11;
	public readonly Input Input = input;
}

/// <summary>Records D3D12 work into the open command list; every texture arrives in COMMON state and has to be left in it</summary>
public unsafe delegate void RecordDelegate(ID3D12GraphicsCommandList* list);

/// <summary>
/// A D3D12 device next to the game's D3D11 one, for the D3D12 libraries (upscalers, frame generation)
/// Game textures the game created NT shared (Game/SharedTargets) are opened directly, others are copied into shared twins,
/// depth through a shader into R32_FLOAT since the libraries take no 24 bit depth; a shared fence orders both queues
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
	private readonly ID3D12CommandAllocator*[] allocators = new ID3D12CommandAllocator*[Frames];
	private readonly ID3D12GraphicsCommandList*[] lists = new ID3D12GraphicsCommandList*[Frames];
	private readonly ulong[] frameFence = new ulong[Frames];
	private readonly ID3D11ComputeShader* depthShader;

	// D3D12 timestamps around the recorded work, two per frame slot
	private readonly ID3D12QueryHeap* queryHeap;
	private readonly ID3D12Resource* readback;
	private readonly ulong* timestamps;
	private readonly ulong timestampFrequency;
	private readonly bool[] measured = new bool[Frames];
	private ulong fenceValue;
	private int frame;

	private ID3D11DeviceContext4* context4;
	private ID3D11DeviceContext* context4Source;

	// Depth copy source view, by texture
	private ID3D11Texture2D* depthSource;
	private ID3D11ShaderResourceView* depthSourceView;
	private ID3D11UnorderedAccessView* depthUav;
	private ID3D11Texture2D* depthUavTexture;

	// Game textures by pointer, null when not shareable, each holding a reference so the address can't be reused by a
	// reallocation while cached; unused entries are released
	private readonly Dictionary<nint, Twin?> direct = [];
	private readonly Dictionary<nint, int> directSeen = [];
	private int executeCount;

	public Bridge(ID3D11Device* device)
	{
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
			adapter->Release();
			ThrowIfFailed(deviceResult);
			this.device12 = createdDevice;

			D3D12_COMMAND_QUEUE_DESC queueDesc = new() { Type = D3D12_COMMAND_LIST_TYPE_DIRECT };
			ID3D12CommandQueue* createdQueue;
			ThrowIfFailed(this.device12->CreateCommandQueue(&queueDesc, __uuidof<ID3D12CommandQueue>(), (void**)&createdQueue));
			this.queue = createdQueue;

			ID3D12Fence* createdFence;
			ThrowIfFailed(this.device12->CreateFence(0, D3D12_FENCE_FLAG_SHARED, __uuidof<ID3D12Fence>(), (void**)&createdFence));
			this.fence12 = createdFence;
			HANDLE fenceHandle;
			ThrowIfFailed(this.device12->CreateSharedHandle((ID3D12DeviceChild*)this.fence12, null, GENERIC_ALL, null, &fenceHandle));
			ID3D11Fence* openedFence;
			HRESULT openResult = this.device11->OpenSharedFence(fenceHandle, __uuidof<ID3D11Fence>(), (void**)&openedFence);
			CloseHandle(fenceHandle);
			ThrowIfFailed(openResult);
			this.fence11 = openedFence;

			for (int i = 0; i < Frames; i++)
			{
				ID3D12CommandAllocator* allocator;
				ThrowIfFailed(this.device12->CreateCommandAllocator(D3D12_COMMAND_LIST_TYPE_DIRECT, __uuidof<ID3D12CommandAllocator>(), (void**)&allocator));
				this.allocators[i] = allocator;
				ID3D12GraphicsCommandList* list;
				ThrowIfFailed(this.device12->CreateCommandList(0, D3D12_COMMAND_LIST_TYPE_DIRECT, allocator, null,
					__uuidof<ID3D12GraphicsCommandList>(), (void**)&list));
				this.lists[i] = list;
				ThrowIfFailed(list->Close());
			}

			D3D12_QUERY_HEAP_DESC heapDesc = new() { Type = D3D12_QUERY_HEAP_TYPE_TIMESTAMP, Count = Frames * 2 };
			ID3D12QueryHeap* createdHeap;
			ThrowIfFailed(this.device12->CreateQueryHeap(&heapDesc, __uuidof<ID3D12QueryHeap>(), (void**)&createdHeap));
			this.queryHeap = createdHeap;

			D3D12_HEAP_PROPERTIES readbackHeap = new(D3D12_HEAP_TYPE_READBACK);
			D3D12_RESOURCE_DESC readbackDesc = D3D12_RESOURCE_DESC.Buffer(Frames * 2 * sizeof(ulong));
			ID3D12Resource* createdReadback;
			ThrowIfFailed(this.device12->CreateCommittedResource(&readbackHeap, D3D12_HEAP_FLAG_NONE, &readbackDesc, D3D12_RESOURCE_STATE_COPY_DEST,
				null, __uuidof<ID3D12Resource>(), (void**)&createdReadback));
			this.readback = createdReadback;
			void* mapped; // Persistent, fine for readback heaps
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

	/// <summary>GPU time of the recorded work, averaged, when measured</summary>
	public double RecordedMs { get; private set; }

	/// <summary>CPU time waiting for a free allocator, averaged, when measured</summary>
	public double CpuStallMs { get; private set; }

	/// <summary>
	/// Hands the D3D11 work so far to D3D12, runs what record records, and has D3D11 wait for it. A failing record still
	/// completes the fence handoff, D3D11 waits on it; the exception is rethrown after
	/// </summary>
	public void Execute(ID3D11DeviceContext* context, RecordDelegate record, bool measure)
	{
		if (context != this.context4Source)
		{
			Dx.Release(this.context4);
			this.context4 = Dx.Query<ID3D11DeviceContext4>(context);
			this.context4Source = context;
		}

		this.executeCount++;
		if (this.executeCount % DirectPruneFrames == 0)
			this.PruneDirect();

		ThrowIfFailed(this.context4->Signal(this.fence11, ++this.fenceValue));
		context->Flush();

		// The allocator is free once the frame that last used it is done
		int slot = this.frame;
		this.frame = (this.frame + 1) % Frames;
		ID3D12GraphicsCommandList* list = this.lists[slot];
		long waitStart = Stopwatch.GetTimestamp();
		this.CpuWait(this.frameFence[slot]);
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
			record(list);
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

		ThrowIfFailed(this.queue->Wait(this.fence12, this.fenceValue));
		if (failure == null)
			this.queue->ExecuteCommandLists(1, (ID3D12CommandList**)&list);
		ThrowIfFailed(this.queue->Signal(this.fence12, ++this.fenceValue));
		this.frameFence[slot] = this.fenceValue;

		// D3D11 waits on the GPU
		ThrowIfFailed(this.context4->Wait(this.fence11, this.fenceValue));
		failure?.Throw();
	}

	/// <summary>Blocks until the D3D12 fence reaches the value</summary>
	private void CpuWait(ulong value)
	{
		if (this.fence12->GetCompletedValue() < value)
			ThrowIfFailed(this.fence12->SetEventOnCompletion(value, HANDLE.NULL));
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

	/// <summary>Drops game textures not seen lately, the D3D12 queue is long done with them</summary>
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

	/// <summary>Top left of the source, as much as both textures hold</summary>
	public static void Copy(ID3D11DeviceContext* context, ID3D11Texture2D* destination, ID3D11Texture2D* source, in Input twin)
	{
		D3D11_TEXTURE2D_DESC desc = Dx.GetDesc(source);
		D3D11_BOX box = new() { right = Math.Min(desc.Width, twin.Width), bottom = Math.Min(desc.Height, twin.Height), back = 1 };
		context->CopySubresourceRegion((ID3D11Resource*)destination, 0, 0, 0, 0, (ID3D11Resource*)source, 0, &box);
	}

	/// <summary>Game depth (24 bit) into an R32_FLOAT twin, the top left width x height</summary>
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

	/// <summary>Waits for the queue; callers dispose their library contexts and twins first or after, the queue is idle</summary>
	public void WaitIdle()
	{
		if (this.queue != null && this.fence12 != null)
		{
			this.queue->Signal(this.fence12, ++this.fenceValue);
			this.CpuWait(this.fenceValue);
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
		Dx.Release(this.queue);
		Dx.Release(this.device12);
		Dx.Release(this.device11);
		GC.SuppressFinalize(this);
	}
}
