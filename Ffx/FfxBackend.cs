using System;
using System.IO;
using System.Runtime.InteropServices;

using TerraFX.Interop.DirectX;

using UpscaleBuddy.D3D12;
using UpscaleBuddy.Fsr3;

using static TerraFX.Interop.DirectX.DXGI_FORMAT;

namespace UpscaleBuddy.Ffx;

/// <summary>
/// AMD's FSR DLLs (FidelityFX SDK 2.x): FSR 4 where the provider supports it, FSR 3.1 elsewhere
/// </summary>
public unsafe class FfxBackend: IBackend
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

	private delegate void MessageDelegate(uint type, char* message);

	// Kept alive here, the DLL calls it from any context
	private static readonly MessageDelegate MessageCallback = OnMessage;

	private static delegate* unmanaged<nint*, Header*, void*, uint> createContext;
	private static delegate* unmanaged<nint*, void*, uint> destroyContext;
	private static delegate* unmanaged<nint*, Header*, uint> query;
	private static delegate* unmanaged<nint*, Header*, uint> dispatch;

	private nint context;

	public FfxBackend(ID3D12Device* device, uint maxRenderWidth, uint maxRenderHeight, uint upscaleWidth, uint upscaleHeight, bool infiniteFar)
	{
		LoadLibrary();

		BackendDx12 backend = new() { Header = { Type = DescBackendDx12 }, Device = device };
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
		nint created;
		Check(createContext(&created, &create.Header, null), "ffxCreateContext");
		this.context = created;

		ProviderVersion provider = new() { Header = { Type = DescQueryProviderVersion } };
		this.Name = query(&created, &provider.Header) == 0 && provider.VersionName != null
			? $"FSR {Marshal.PtrToStringAnsi((nint)provider.VersionName)} (AMD DLL, D3D12)"
			: "FSR (AMD DLL, D3D12)";
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

		nint current = this.context;
		Check(dispatch(&current, &desc.Header), "ffxDispatch");
	}

	/// <summary>FFX transitions from and back to the given state itself</summary>
	private static Resource Describe(in Input input) => new()
	{
		Pointer = input.Resource, Type = ResourceTypeTexture2D, Format = FfxFormat(input.Format), Width = input.Width, Height = input.Height,
		Depth = 1, MipCount = 1, Usage = input.UnorderedAccess ? ResourceUsageUav : 0, State = ResourceStateCommon,
	};

	/// <summary>FfxApiSurfaceFormat, SDK 2.3</summary>
	private static uint FfxFormat(DXGI_FORMAT format) => format switch
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
		_ => throw new NotSupportedException($"{format} has no FFX equivalent here"),
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

	/// <summary>D3D12Upscaler waits for the queue first</summary>
	public void Dispose()
	{
		if (this.context != 0)
		{
			nint current = this.context;
			destroyContext(&current, null);
			this.context = 0;
		}

		GC.SuppressFinalize(this);
	}
}
