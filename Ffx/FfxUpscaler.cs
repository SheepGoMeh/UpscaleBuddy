using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;

using UpscaleBuddy.Fsr3;

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

namespace UpscaleBuddy.Ffx;

/// <summary>
/// AMD's FSR DLLs (FidelityFX SDK 2.x, FSR 4 where supported, FSR 3.1 elsewhere) on a D3D12 device next to the game's D3D11 one
/// Game textures the game created NT shared (Game/SharedTargets) are opened directly, others are copied into shared twins,
/// depth through a shader since FFX takes no 24 bit depth; a shared fence orders both queues
/// </summary>
public unsafe class FfxUpscaler: IUpscaler
{
	private const string LoaderDll = "amd_fidelityfx_loader_dx12.dll";
	private const string UpscalerDll = "amd_fidelityfx_upscaler_dx12.dll";

	private const ulong DescCreateUpscale = 0x00010000;
	private const ulong DescCreateUpscaleVersion = 0x0001000B;
	private const ulong DescDispatchUpscale = 0x00010001;
	private const ulong DescBackendDx12 = 0x2;
	private const ulong DescQueryProviderVersion = 6;
	private const uint UpscalerVersion = (4 << 22) | (1 << 12) | 1; // FFX_UPSCALER_VERSION 4.1.1, SDK 2.3

	private const uint FlagDepthInverted = 1 << 3;
	private const uint FlagDepthInfinite = 1 << 4;
	private const uint ResourceTypeTexture2D = 2;
	private const uint ResourceUsageUav = 1 << 1;
	private const uint ResourceStateCommon = 1 << 0;
	private const int Frames = 8; // Above the swapchain's frame latency, so Present throttles rather than the allocators
	private const int DirectPruneFrames = 120;

	[StructLayout(LayoutKind.Sequential)]
	private struct Header
	{
		public ulong Type;
		public Header* Next;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct CreateUpscale
	{
		public Header Header;
		public uint Flags;
		public uint MaxRenderWidth, MaxRenderHeight;
		public uint MaxUpscaleWidth, MaxUpscaleHeight;
		public nint Message;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct CreateUpscaleVersion
	{
		public Header Header;
		public uint Version;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct BackendDx12
	{
		public Header Header;
		public ID3D12Device* Device;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct ProviderVersion
	{
		public Header Header;
		public ulong VersionId;
		public byte* VersionName;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct Resource
	{
		public ID3D12Resource* Pointer;
		public uint Type, Format, Width, Height, Depth, MipCount, Flags, Usage;
		public uint State;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct DispatchUpscale
	{
		public Header Header;
		public ID3D12GraphicsCommandList* CommandList;
		public Resource Color, Depth, MotionVectors, Exposure, Reactive, TransparencyAndComposition, Output;
		public float JitterX, JitterY;
		public float MotionVectorScaleX, MotionVectorScaleY;
		public uint RenderWidth, RenderHeight;
		public uint UpscaleWidth, UpscaleHeight;
		public byte EnableSharpening;
		public float Sharpness;
		public float FrameTimeDelta;
		public float PreExposure;
		public byte Reset;
		public float CameraNear;
		public float CameraFar;
		public float CameraFovAngleVertical;
		public float ViewSpaceToMetersFactor;
		public uint Flags;
	}

	/// <summary>D3D12 view of a D3D11 texture: our own twin, or the game's texture itself when it's shared</summary>
	private sealed class Twin(ID3D11Texture2D* texture11, ID3D12Resource* resource12, Resource description)
	{
		public readonly ID3D11Texture2D* Texture11 = texture11;
		public readonly ID3D12Resource* Resource12 = resource12;
		public readonly Resource Description = description;
	}

	private delegate void MessageDelegate(uint type, char* message);

	// Kept alive here, the DLL calls it from any context
	private static readonly MessageDelegate MessageCallback = OnMessage;

	private static delegate* unmanaged<nint*, Header*, void*, uint> createContext;
	private static delegate* unmanaged<nint*, void*, uint> destroyContext;
	private static delegate* unmanaged<nint*, Header*, uint> query;
	private static delegate* unmanaged<nint*, Header*, uint> dispatch;

	private readonly ID3D11Device5* device11;
	private readonly ID3D12Device* device12;
	private readonly ID3D12CommandQueue* queue;
	private readonly ID3D12Fence* fence12;
	private readonly ID3D11Fence* fence11;
	private readonly ID3D12CommandAllocator*[] allocators = new ID3D12CommandAllocator*[Frames];
	private readonly ID3D12GraphicsCommandList*[] lists = new ID3D12GraphicsCommandList*[Frames];
	private readonly ulong[] frameFence = new ulong[Frames];
	private readonly ID3D11ComputeShader* depthShader;

	// D3D11 marks: start, inputs copied, D3D12 done, output copied; D3D12: around ffxDispatch, two per frame slot
	private readonly GpuTimer timer;
	private readonly ID3D12QueryHeap* queryHeap;
	private readonly ID3D12Resource* readback;
	private readonly ulong* timestamps;
	private readonly ulong timestampFrequency;
	private readonly bool[] measured = new bool[Frames];
	private double fsrMs;
	private double cpuStallMs;
	private nint ffxContext;
	private ulong fenceValue;
	private int frame;

	// Created on the first dispatch, in the game's formats
	private Twin? color, depth, motionVectors, output;
	private ID3D11Texture2D* depthSource;
	private ID3D11ShaderResourceView* depthSourceView;
	private ID3D11UnorderedAccessView* depthUav;
	private ID3D11DeviceContext4* context4;
	private ID3D11DeviceContext* context4Source;

	// Game textures by pointer, null when not shareable, each holding a reference so the address can't be reused by a
	// reallocation while cached; unused entries are released
	private readonly Dictionary<nint, Twin?> direct = [];
	private readonly Dictionary<nint, int> directSeen = [];
	private int dispatchCount;
	private bool directColorUsed, directMotionVectorsUsed, directOutputUsed;

	public FfxUpscaler(ID3D11Device* device, uint maxRenderWidth, uint maxRenderHeight, uint upscaleWidth, uint upscaleHeight, bool infiniteFar)
	{
		this.MaxRenderWidth = maxRenderWidth;
		this.MaxRenderHeight = maxRenderHeight;
		this.UpscaleWidth = upscaleWidth;
		this.UpscaleHeight = upscaleHeight;
		LoadLibrary();

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

			this.timer = new GpuTimer(device, 4);
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

			BackendDx12 backend = new() { Header = { Type = DescBackendDx12 }, Device = this.device12 };
			CreateUpscaleVersion version = new() { Header = { Type = DescCreateUpscaleVersion, Next = &backend.Header }, Version = UpscalerVersion };
			CreateUpscale create = new()
			{
				Header = { Type = DescCreateUpscale, Next = &version.Header },
				// Same permutation as the built-in shaders: LDR, low resolution MVs, inverted depth
				Flags = FlagDepthInverted | (infiniteFar ? FlagDepthInfinite : 0),
				MaxRenderWidth = maxRenderWidth, MaxRenderHeight = maxRenderHeight,
				MaxUpscaleWidth = upscaleWidth, MaxUpscaleHeight = upscaleHeight,
				Message = Marshal.GetFunctionPointerForDelegate(MessageCallback),
			};
			nint context;
			Check(createContext(&context, &create.Header, null), "ffxCreateContext");
			this.ffxContext = context;

			ProviderVersion provider = new() { Header = { Type = DescQueryProviderVersion } };
			this.Name = query(&context, &provider.Header) == 0 && provider.VersionName != null
				? $"FSR {Marshal.PtrToStringAnsi((nint)provider.VersionName)} (AMD DLL, D3D12)"
				: "FSR (AMD DLL, D3D12)";
		}
		catch
		{
			this.Dispose();
			throw;
		}
	}

	public string Name { get; } = "";

	public uint MaxRenderWidth { get; }

	public uint MaxRenderHeight { get; }

	public uint UpscaleWidth { get; }

	public uint UpscaleHeight { get; }

	/// <summary>Handoff = D3D11 waiting on D3D12 minus FSR itself: fence latency, queue switches, late D3D12 submission</summary>
	public string Timings
	{
		get
		{
			double[] ms = this.timer.Ms;
			return $"GPU {ms[0] + ms[1] + ms[2]:F3} ms = copy in {ms[0]:F3} + FSR {this.fsrMs:F3} + handoff {ms[1] - this.fsrMs:F3} + copy out {ms[2]:F3}, " +
			       $"CPU stall on allocators {this.cpuStallMs:F3} ms, zero copy: color {this.directColorUsed}, motion vectors {this.directMotionVectorsUsed}, output {this.directOutputUsed}";
		}
	}

	public void Dispatch(ID3D11DeviceContext* context, in Fsr3Upscaler.DispatchParams p)
	{
		if (p.Measure)
			this.timer.Begin(context);

		if (context != this.context4Source)
		{
			Dx.Release(this.context4);
			this.context4 = Dx.Query<ID3D11DeviceContext4>(context);
			this.context4Source = context;
		}

		this.dispatchCount++;
		if (this.dispatchCount % DirectPruneFrames == 0)
			this.PruneDirect();

		Twin? directColor = this.Direct(p.Color);
		Twin? directMotionVectors = this.Direct(p.MotionVectors);
		Twin? directOutput = this.Direct(p.Output);
		this.directColorUsed = directColor != null;
		this.directMotionVectorsUsed = directMotionVectors != null;
		this.directOutputUsed = directOutput != null;

		Twin colorTwin = directColor ?? (this.color ??= this.CreateTwin(Dx.GetDesc(p.Color).Format, this.MaxRenderWidth, this.MaxRenderHeight));
		Twin motionVectorsTwin = directMotionVectors ??
		                         (this.motionVectors ??= this.CreateTwin(Dx.GetDesc(p.MotionVectors).Format, this.MaxRenderWidth, this.MaxRenderHeight));
		Twin outputTwin = directOutput ?? (this.output ??= this.CreateTwin(Dx.GetDesc(p.Output).Format, this.UpscaleWidth, this.UpscaleHeight));
		this.depth ??= this.CreateTwin(DXGI_FORMAT_R32_FLOAT, this.MaxRenderWidth, this.MaxRenderHeight);

		// D3D11: game textures into the twins unless D3D12 reads them directly, then hand over to the D3D12 queue
		if (directColor == null)
			Copy(context, colorTwin.Texture11, p.Color, colorTwin.Description);
		if (directMotionVectors == null)
			Copy(context, motionVectorsTwin.Texture11, p.MotionVectors, motionVectorsTwin.Description);
		this.CopyDepth(context, p.Depth);
		if (p.Measure)
			this.timer.Mark(context, 1);
		ThrowIfFailed(this.context4->Signal(this.fence11, ++this.fenceValue));
		context->Flush();

		// D3D12: the allocator is free once the frame that last used it is done
		int slot = this.frame;
		this.frame = (this.frame + 1) % Frames;
		ID3D12GraphicsCommandList* list = this.lists[slot];
		long waitStart = Stopwatch.GetTimestamp();
		this.CpuWait(this.frameFence[slot]);
		if (p.Measure)
			this.cpuStallMs = (this.cpuStallMs * 0.9) + (Stopwatch.GetElapsedTime(waitStart).TotalMilliseconds * 0.1);
		if (this.measured[slot])
		{
			ulong elapsed = this.timestamps[(slot * 2) + 1] - this.timestamps[slot * 2];
			this.fsrMs = (this.fsrMs * 0.9) + (elapsed * 1000.0 / this.timestampFrequency * 0.1);
			this.measured[slot] = false;
		}

		ThrowIfFailed(this.allocators[slot]->Reset());
		ThrowIfFailed(list->Reset(this.allocators[slot], null));
		if (p.Measure)
			list->EndQuery(this.queryHeap, D3D12_QUERY_TYPE_TIMESTAMP, (uint)slot * 2);

		DispatchUpscale desc = new()
		{
			Header = { Type = DescDispatchUpscale },
			CommandList = list,
			Color = colorTwin.Description,
			Depth = this.depth.Description,
			MotionVectors = motionVectorsTwin.Description,
			Output = outputTwin.Description,
			JitterX = p.JitterX,
			JitterY = p.JitterY,
			MotionVectorScaleX = p.MotionVectorScaleX,
			MotionVectorScaleY = p.MotionVectorScaleY,
			RenderWidth = p.RenderWidth,
			RenderHeight = p.RenderHeight,
			UpscaleWidth = this.UpscaleWidth,
			UpscaleHeight = this.UpscaleHeight,
			EnableSharpening = p.Sharpen ? (byte)1 : (byte)0,
			Sharpness = p.Sharpness,
			FrameTimeDelta = p.FrameTimeMs,
			PreExposure = p.PreExposure != 0.0f ? p.PreExposure : 1.0f,
			Reset = p.Reset ? (byte)1 : (byte)0,
			CameraNear = p.CameraNear,
			CameraFar = p.CameraFar,
			CameraFovAngleVertical = p.CameraFovY,
			ViewSpaceToMetersFactor = 1.0f,
		};

		nint ffx = this.ffxContext;
		uint result;
		try
		{
			result = dispatch(&ffx, &desc.Header);
			if (p.Measure && result == 0)
			{
				list->EndQuery(this.queryHeap, D3D12_QUERY_TYPE_TIMESTAMP, ((uint)slot * 2) + 1);
				list->ResolveQueryData(this.queryHeap, D3D12_QUERY_TYPE_TIMESTAMP, (uint)slot * 2, 2, this.readback, (ulong)slot * 2 * sizeof(ulong));
				this.measured[slot] = true;
			}
		}
		finally
		{
			ThrowIfFailed(list->Close());
		}

		ThrowIfFailed(this.queue->Wait(this.fence12, this.fenceValue));
		if (result == 0)
			this.queue->ExecuteCommandLists(1, (ID3D12CommandList**)&list);
		ThrowIfFailed(this.queue->Signal(this.fence12, ++this.fenceValue));
		this.frameFence[slot] = this.fenceValue;

		// D3D11 waits on the GPU, the output twin back into the game's output
		ThrowIfFailed(this.context4->Wait(this.fence11, this.fenceValue));
		Check(result, "ffxDispatch");
		if (p.Measure)
			this.timer.Mark(context, 2);
		if (directOutput == null)
			Copy(context, p.Output, outputTwin.Texture11, outputTwin.Description);
		if (p.Measure)
		{
			this.timer.Mark(context, 3);
			this.timer.End(context);
		}
	}

	/// <summary>Blocks until the D3D12 fence reaches the value</summary>
	private void CpuWait(ulong value)
	{
		if (this.fence12->GetCompletedValue() < value)
			ThrowIfFailed(this.fence12->SetEventOnCompletion(value, HANDLE.NULL));
	}

	/// <summary>The game's texture opened on D3D12, null when the game didn't create it shared</summary>
	private Twin? Direct(ID3D11Texture2D* texture)
	{
		if (!this.direct.TryGetValue((nint)texture, out Twin? twin))
		{
			texture->AddRef();
			this.direct[(nint)texture] = null;
			D3D11_TEXTURE2D_DESC desc = Dx.GetDesc(texture);
			uint format = FfxFormatOrZero(desc.Format);
			if ((desc.MiscFlags & Dx.MiscSharedNtHandle) == Dx.MiscSharedNtHandle && format != 0 && desc.MipLevels == 1)
			{
				ID3D12Resource* resource = this.OpenOnD3D12(texture);
				twin = new Twin(texture, resource, new Resource
				{
					Pointer = resource, Type = ResourceTypeTexture2D, Format = format, Width = desc.Width, Height = desc.Height,
					Depth = 1, MipCount = 1, Usage = (desc.BindFlags & (uint)D3D11_BIND_UNORDERED_ACCESS) != 0 ? ResourceUsageUav : 0,
					State = ResourceStateCommon,
				});
			}

			this.direct[(nint)texture] = twin;
		}

		this.directSeen[(nint)texture] = this.dispatchCount;
		return twin;
	}

	/// <summary>Drops game textures not seen lately, the D3D12 queue is long done with them</summary>
	private void PruneDirect()
	{
		List<nint> stale = [];
		foreach ((nint texture, int seen) in this.directSeen)
		{
			if (this.dispatchCount - seen >= DirectPruneFrames)
				stale.Add(texture);
		}

		foreach (nint texture in stale)
			this.ReleaseDirect(texture);
	}

	private void ReleaseDirect(nint texture)
	{
		if (this.direct[texture] is { } twin)
			Dx.Release(twin.Resource12);
		Dx.Release((ID3D11Texture2D*)texture);
		this.direct.Remove(texture);
		this.directSeen.Remove(texture);
	}

	/// <summary>Our NT shared texture, opened on D3D12</summary>
	private Twin CreateTwin(DXGI_FORMAT format, uint width, uint height)
	{
		ID3D11Texture2D* texture = Dx.CreateTexture((ID3D11Device*)this.device11, width, height, format, 1, Dx.BindShaderResourceAndUav,
			misc: Dx.MiscSharedNtHandle);
		try
		{
			ID3D12Resource* resource = this.OpenOnD3D12(texture);
			return new Twin(texture, resource, new Resource
			{
				Pointer = resource, Type = ResourceTypeTexture2D, Format = FfxFormat(format), Width = width, Height = height,
				Depth = 1, MipCount = 1, Usage = ResourceUsageUav, State = ResourceStateCommon,
			});
		}
		catch
		{
			texture->Release();
			throw;
		}
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
	private static void Copy(ID3D11DeviceContext* context, ID3D11Texture2D* destination, ID3D11Texture2D* source, in Resource twin)
	{
		D3D11_TEXTURE2D_DESC desc = Dx.GetDesc(source);
		D3D11_BOX box = new() { right = Math.Min(desc.Width, twin.Width), bottom = Math.Min(desc.Height, twin.Height), back = 1 };
		context->CopySubresourceRegion((ID3D11Resource*)destination, 0, 0, 0, 0, (ID3D11Resource*)source, 0, &box);
	}

	private void CopyDepth(ID3D11DeviceContext* context, ID3D11Texture2D* source)
	{
		// The view keeps the texture alive, so a new depth texture can't get this address
		if (source != this.depthSource)
		{
			Dx.Release(this.depthSourceView);
			this.depthSourceView = null;
			this.depthSource = source;
			this.depthSourceView = Dx.CreateSrv((ID3D11Device*)this.device11, source, Dx.ReadableFormat(Dx.GetDesc(source).Format));
		}

		if (this.depthUav == null)
			this.depthUav = Dx.CreateUav((ID3D11Device*)this.device11, this.depth!.Texture11, DXGI_FORMAT_R32_FLOAT);

		ID3D11ShaderResourceView* srv = this.depthSourceView;
		ID3D11UnorderedAccessView* uav = this.depthUav;
		ID3D11ShaderResourceView* noSrv = null;
		ID3D11UnorderedAccessView* noUav = null;
		context->CSSetShaderResources(0, 1, &srv);
		context->CSSetUnorderedAccessViews(0, 1, &uav, null);
		context->CSSetShader(this.depthShader, null, 0);
		context->Dispatch((this.MaxRenderWidth + 7) / 8, (this.MaxRenderHeight + 7) / 8, 1);
		context->CSSetUnorderedAccessViews(0, 1, &noUav, null);
		context->CSSetShaderResources(0, 1, &noSrv);
		context->CSSetShader(null, null, 0);
	}

	private static uint FfxFormat(DXGI_FORMAT format) =>
		FfxFormatOrZero(format) is var ffx and not 0 ? ffx : throw new NotSupportedException($"{format} has no FFX equivalent here");

	/// <summary>FfxApiSurfaceFormat, SDK 2.3</summary>
	private static uint FfxFormatOrZero(DXGI_FORMAT format) => format switch
	{
		DXGI_FORMAT_R32G32B32A32_FLOAT => 3,
		DXGI_FORMAT_R16G16B16A16_FLOAT => 4,
		DXGI_FORMAT_R32G32_FLOAT => 6,
		DXGI_FORMAT_R10G10B10A2_UNORM => 17,
		DXGI_FORMAT_R11G11B10_FLOAT => 16,
		DXGI_FORMAT_R8G8B8A8_UNORM => 10,
		DXGI_FORMAT_R16G16_FLOAT => 18,
		DXGI_FORMAT_R32_FLOAT => 28,
		DXGI_FORMAT_R16_FLOAT => 21,
		DXGI_FORMAT_B8G8R8A8_UNORM => 14,
		_ => 0,
	};

	/// <summary>
	/// Upscaler DLL first by full path, the loader finds it by name
	/// </summary>
	private static void LoadLibrary()
	{
		if (createContext != null)
			return;

		string directory = Service.PluginInterface.AssemblyLocation.DirectoryName!;
		NativeLibrary.Load(Path.Combine(directory, UpscalerDll));
		nint loader = NativeLibrary.Load(Path.Combine(directory, LoaderDll));
		query = (delegate* unmanaged<nint*, Header*, uint>)NativeLibrary.GetExport(loader, "ffxQuery");
		dispatch = (delegate* unmanaged<nint*, Header*, uint>)NativeLibrary.GetExport(loader, "ffxDispatch");
		destroyContext = (delegate* unmanaged<nint*, void*, uint>)NativeLibrary.GetExport(loader, "ffxDestroyContext");
		createContext = (delegate* unmanaged<nint*, Header*, void*, uint>)NativeLibrary.GetExport(loader, "ffxCreateContext");
	}

	private static void OnMessage(uint type, char* message)
	{
		string text = new(message);
		if (type == 0)
			Service.PluginLog.Error($"FFX: {text}");
		else
			Service.PluginLog.Warning($"FFX: {text}");
	}

	private static void Check(uint result, string what)
	{
		if (result != 0)
			throw new InvalidOperationException($"{what} failed: {result}");
	}

	public void Dispose()
	{
		// The D3D12 queue can still be running the last frames
		if (this.queue != null && this.fence12 != null)
		{
			this.queue->Signal(this.fence12, ++this.fenceValue);
			this.CpuWait(this.fenceValue);
		}

		if (this.ffxContext != 0)
		{
			nint context = this.ffxContext;
			destroyContext(&context, null);
			this.ffxContext = 0;
		}

		foreach (Twin? twin in new[] { this.color, this.depth, this.motionVectors, this.output })
		{
			if (twin == null)
				continue;

			Dx.Release(twin.Resource12);
			Dx.Release(twin.Texture11);
		}

		this.color = this.depth = this.motionVectors = this.output = null;
		foreach (nint texture in new List<nint>(this.direct.Keys))
			this.ReleaseDirect(texture);

		foreach (ID3D12GraphicsCommandList* list in this.lists)
			Dx.Release(list);
		foreach (ID3D12CommandAllocator* allocator in this.allocators)
			Dx.Release(allocator);
		this.timer?.Dispose();
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
