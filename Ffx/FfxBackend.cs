using System;
using System.Runtime.InteropServices;

using TerraFX.Interop.DirectX;

using UpscaleBuddy.D3D12;
using UpscaleBuddy.Fsr3;

using static UpscaleBuddy.Ffx.FfxApi;

namespace UpscaleBuddy.Ffx;

/// <summary>
/// AMD's FSR DLLs (FidelityFX SDK 2.x): FSR 4 where the provider supports it, FSR 3.1 elsewhere
/// </summary>
public unsafe class FfxBackend: IBackend
{
	private const string UpscalerDll = "amd_fidelityfx_upscaler_dx12.dll";

	private const ulong DescCreateUpscale = 0x00010000;
	private const ulong DescCreateUpscaleVersion = 0x0001000B;
	private const ulong DescDispatchUpscale = 0x00010001;
	private const uint UpscalerVersion = (4 << 22) | (1 << 12) | 1; // FFX_UPSCALER_VERSION 4.1.1, SDK 2.3

	private const uint FlagDepthInverted = 1 << 3;
	private const uint FlagDepthInfinite = 1 << 4;

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

	private nint context;

	public FfxBackend(ID3D12Device* device, uint maxRenderWidth, uint maxRenderHeight, uint upscaleWidth, uint upscaleHeight, bool infiniteFar)
	{
		Load(UpscalerDll);

		BackendDx12 backend = new() { Header = { Type = DescBackendDx12 }, Device = device };
		CreateUpscaleVersion version = new() { Header = { Type = DescCreateUpscaleVersion, Next = &backend.Header }, Version = UpscalerVersion };
		CreateUpscale create = new()
		{
			Header = { Type = DescCreateUpscale, Next = &version.Header },
			// Same permutation as the built-in shaders: LDR, low resolution MVs, inverted depth
			Flags = FlagDepthInverted | (infiniteFar ? FlagDepthInfinite : 0),
			MaxRenderWidth = maxRenderWidth, MaxRenderHeight = maxRenderHeight,
			MaxUpscaleWidth = upscaleWidth, MaxUpscaleHeight = upscaleHeight,
			Message = FfxApi.Message,
		};
		this.context = CreateContext(&create.Header, "ffxCreateContext");

		string? provider = ProviderName(this.context);
		this.Name = provider != null ? $"FSR {provider} (AMD DLL, D3D12)" : "FSR (AMD DLL, D3D12)";
	}

	public string Name { get; }

	public string Label => "FSR";

	public void Record(ID3D12GraphicsCommandList* list, in Input color, in Input motionVectors, in Input depth, in Input output,
		in Fsr3Upscaler.DispatchParams p, uint upscaleWidth, uint upscaleHeight)
	{
		DispatchUpscale desc = new()
		{
			Header = { Type = DescDispatchUpscale },
			CommandList = list,
			Color = Describe(color),
			Depth = Describe(depth),
			MotionVectors = Describe(motionVectors),
			Output = Describe(output),
			JitterX = p.JitterX,
			JitterY = p.JitterY,
			MotionVectorScaleX = p.MotionVectorScaleX,
			MotionVectorScaleY = p.MotionVectorScaleY,
			RenderWidth = p.RenderWidth,
			RenderHeight = p.RenderHeight,
			UpscaleWidth = upscaleWidth,
			UpscaleHeight = upscaleHeight,
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

		Dispatch(this.context, &desc.Header, "ffxDispatch");
	}

	/// <summary>D3D12Upscaler waits for the queue first</summary>
	public void Dispose()
	{
		Destroy(ref this.context);
		GC.SuppressFinalize(this);
	}
}
