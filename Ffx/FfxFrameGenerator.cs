using System;
using System.Numerics;
using System.Runtime.InteropServices;

using TerraFX.Interop.DirectX;

using UpscaleBuddy.D3D12;
using UpscaleBuddy.FrameGeneration;

using static TerraFX.Interop.DirectX.DXGI_FORMAT;
using static UpscaleBuddy.Ffx.FfxApi;

namespace UpscaleBuddy.Ffx;

/// <summary>
/// AMD's frame generation DLL (FidelityFX SDK 2.x) over the bridge: FSR 4 frame generation where the provider supports it
/// (RX 9000), FSR 3.1 elsewhere. Without AMD's swapchain (FFX_FRAMEGENERATION_FLAG_NO_SWAPCHAIN_CONTEXT_NOTIFY): configure,
/// prepare and dispatch each frame, the generated frame into our own output
/// </summary>
public unsafe class FfxFrameGenerator: IFrameGenerator
{
	private const string FrameGenerationDll = "amd_fidelityfx_framegeneration_dx12.dll";

	private const ulong DescCreateFrameGeneration = 0x00020001;
	private const ulong DescConfigureFrameGeneration = 0x00020002;
	private const ulong DescDispatchFrameGeneration = 0x00020003;
	private const ulong DescDispatchPrepareV2 = 0x0002000C;
	private const ulong DescCreateFrameGenerationVersion = 0x0002000E;
	private const uint FrameGenerationVersion = (4 << 22) | (0 << 12) | 1; // FFX_FRAMEGENERATION_VERSION 4.0.1, SDK 2.3

	private const uint CreateDepthInverted = 1 << 3;
	private const uint CreateDepthInfinite = 1 << 4;
	private const uint DispatchNoSwapchain = 1 << 3;
	private const uint TransferFunctionSrgb = 0;

	[StructLayout(LayoutKind.Sequential)]
	private struct Dimensions
	{
		public uint Width, Height;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct Rect
	{
		public int Left, Top, Width, Height;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct CreateFrameGeneration
	{
		public Header Header;
		public uint Flags;
		public Dimensions DisplaySize;
		public Dimensions MaxRenderSize;
		public uint BackBufferFormat;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct CreateFrameGenerationVersion
	{
		public Header Header;
		public uint Version;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct ConfigureFrameGeneration
	{
		public Header Header;
		public void* SwapChain;
		public nint PresentCallback, PresentCallbackUserContext;
		public nint FrameGenerationCallback, FrameGenerationCallbackUserContext;
		public byte FrameGenerationEnabled;
		public byte AllowAsyncWorkloads;
		public Resource HudLessColor;
		public uint Flags;
		public byte OnlyPresentGenerated;
		public Rect GenerationRect;
		public ulong FrameId;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct DispatchPrepareV2
	{
		public Header Header;
		public ulong FrameId;
		public uint Flags;
		public ID3D12GraphicsCommandList* CommandList;
		public Dimensions RenderSize;
		public float JitterX, JitterY;
		public float MotionVectorScaleX, MotionVectorScaleY;
		public float FrameTimeDelta;
		public byte Reset;
		public float CameraNear, CameraFar, CameraFovAngleVertical, ViewSpaceToMetersFactor;
		public Resource Depth;
		public Resource MotionVectors;
		public Vector3 CameraPosition, CameraUp, CameraRight, CameraForward;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct DispatchFrameGeneration
	{
		public Header Header;
		public ID3D12GraphicsCommandList* CommandList;
		public Resource PresentColor;
		public Resource Output0, Output1, Output2, Output3;
		public uint NumGeneratedFrames;
		public byte Reset;
		public uint BackbufferTransferFunction;
		public float MinLuminance, MaxLuminance;
		public Rect GenerationRect;
		public ulong FrameId;
	}

	private readonly Bridge bridge;
	private readonly uint width, height;
	private nint context;
	private ulong frameId;
	private bool first = true;

	// Created on the first frame, in the inputs' formats
	private Twin? scene, output, depth, motionVectors;

	public FfxFrameGenerator(ID3D11Device* device, uint width, uint height, uint maxRenderWidth, uint maxRenderHeight, bool infiniteFar)
	{
		this.width = width;
		this.height = height;
		Load(FrameGenerationDll);
		this.bridge = new Bridge(device);
		try
		{
			BackendDx12 backend = new() { Header = { Type = DescBackendDx12 }, Device = this.bridge.Device12 };
			CreateFrameGenerationVersion version = new()
			{
				Header = { Type = DescCreateFrameGenerationVersion, Next = &backend.Header }, Version = FrameGenerationVersion,
			};
			CreateFrameGeneration create = new()
			{
				Header = { Type = DescCreateFrameGeneration, Next = &version.Header },
				// Same as the upscaler: LDR, render size motion vectors without jitter, inverted depth
				Flags = CreateDepthInverted | (infiniteFar ? CreateDepthInfinite : 0),
				DisplaySize = new Dimensions { Width = width, Height = height },
				MaxRenderSize = new Dimensions { Width = maxRenderWidth, Height = maxRenderHeight },
				BackBufferFormat = Format(DXGI_FORMAT_B8G8R8A8_UNORM),
			};
			this.context = CreateContext(&create.Header, "ffxCreateContext (frame generation)");

			string? provider = ProviderName(this.context);
			this.Name = provider != null ? $"FSR {provider} frame generation (AMD DLL, D3D12)" : "FSR frame generation (AMD DLL, D3D12)";
		}
		catch
		{
			this.Dispose();
			throw;
		}
	}

	public string Name { get; } = "FSR frame generation (AMD DLL, D3D12)";

	public void Generate(ID3D11DeviceContext* d3d11, in FrameInputs inputs, ID3D11Texture2D* result)
	{
		D3D11_TEXTURE2D_DESC depthDesc = Dx.GetDesc(inputs.Depth);
		D3D11_TEXTURE2D_DESC motionDesc = Dx.GetDesc(inputs.MotionVectors);
		Twin sceneTwin = this.scene ??= this.bridge.CreateTwin(DXGI_FORMAT_B8G8R8A8_UNORM, this.width, this.height);
		Twin outputTwin = this.output ??= this.bridge.CreateTwin(DXGI_FORMAT_B8G8R8A8_UNORM, this.width, this.height);
		Twin depthTwin = this.depth ??= this.bridge.CreateTwin(DXGI_FORMAT_R32_FLOAT, depthDesc.Width, depthDesc.Height);
		Twin? directMotion = this.bridge.Direct(inputs.MotionVectors);
		Twin motionTwin = directMotion ?? (this.motionVectors ??= this.bridge.CreateTwin(motionDesc.Format, motionDesc.Width, motionDesc.Height));

		// D3D11: inputs into the twins unless D3D12 reads them directly
		Bridge.Copy(d3d11, sceneTwin.Texture11, inputs.Scene, sceneTwin.Input);
		if (directMotion == null)
			Bridge.Copy(d3d11, motionTwin.Texture11, inputs.MotionVectors, motionTwin.Input);
		this.bridge.CopyDepth(d3d11, inputs.Depth, depthTwin, inputs.RenderWidth, inputs.RenderHeight);

		FrameInputs frame = inputs;
		bool reset = inputs.Reset || this.first;
		this.first = false;
		ulong id = ++this.frameId;
		Rect rect = new() { Width = (int)this.width, Height = (int)this.height };
		nint current = this.context;
		this.bridge.Execute(d3d11, list =>
		{
			ConfigureFrameGeneration configure = new()
			{
				Header = { Type = DescConfigureFrameGeneration },
				FrameGenerationEnabled = 1,
				Flags = DispatchNoSwapchain,
				GenerationRect = rect,
				FrameId = id,
			};
			Configure(current, &configure.Header, "ffxConfigure (frame generation)");

			DispatchPrepareV2 prepare = new()
			{
				Header = { Type = DescDispatchPrepareV2 },
				FrameId = id,
				Flags = DispatchNoSwapchain,
				CommandList = list,
				RenderSize = new Dimensions { Width = frame.RenderWidth, Height = frame.RenderHeight },
				JitterX = frame.JitterX,
				JitterY = frame.JitterY,
				MotionVectorScaleX = frame.MotionVectorScaleX,
				MotionVectorScaleY = frame.MotionVectorScaleY,
				FrameTimeDelta = frame.FrameTimeMs,
				Reset = reset ? (byte)1 : (byte)0,
				CameraNear = frame.CameraNear,
				CameraFar = frame.CameraFar,
				CameraFovAngleVertical = frame.CameraFovY,
				ViewSpaceToMetersFactor = 1.0f,
				Depth = Describe(depthTwin.Input),
				MotionVectors = Describe(motionTwin.Input),
				CameraPosition = frame.CameraPosition,
				CameraUp = frame.CameraUp,
				CameraRight = frame.CameraRight,
				CameraForward = frame.CameraForward,
			};
			Dispatch(current, &prepare.Header, "ffxDispatch (frame generation prepare)");

			DispatchFrameGeneration generate = new()
			{
				Header = { Type = DescDispatchFrameGeneration },
				CommandList = list,
				PresentColor = Describe(sceneTwin.Input),
				Output0 = Describe(outputTwin.Input),
				NumGeneratedFrames = 1,
				Reset = reset ? (byte)1 : (byte)0,
				BackbufferTransferFunction = TransferFunctionSrgb,
				GenerationRect = rect,
				FrameId = id,
			};
			Dispatch(current, &generate.Header, "ffxDispatch (frame generation)");
		}, false);

		Bridge.Copy(d3d11, result, outputTwin.Texture11, outputTwin.Input);
	}

	public void Dispose()
	{
		this.bridge?.WaitIdle();
		Destroy(ref this.context);
		Bridge.Release(this.scene);
		Bridge.Release(this.output);
		Bridge.Release(this.depth);
		Bridge.Release(this.motionVectors);
		this.scene = this.output = this.depth = this.motionVectors = null;
		this.bridge?.Dispose();
		GC.SuppressFinalize(this);
	}
}
