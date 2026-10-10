using System;
using System.IO;
using System.Runtime.InteropServices;

using TerraFX.Interop.DirectX;

using UpscaleBuddy.D3D12;

using static TerraFX.Interop.DirectX.DXGI_FORMAT;

namespace UpscaleBuddy.Ffx;

/// <summary>
/// The FFX API (FidelityFX SDK 2.x) through AMD's signed loader: shared structs and the loader's exports. Each effect's
/// DLL is loaded by full path first, the loader then finds it by name
/// </summary>
public static unsafe class FfxApi
{
	public const string LoaderDll = "amd_fidelityfx_loader_dx12.dll";

	public const ulong DescBackendDx12 = 0x2;
	public const ulong DescQueryProviderVersion = 6;

	public const uint ResourceTypeTexture2D = 2;
	public const uint ResourceUsageUav = 1 << 1;
	public const uint ResourceStateCommon = 1 << 0;

	[StructLayout(LayoutKind.Sequential)]
	public struct Header
	{
		public ulong Type;
		public Header* Next;
	}

	[StructLayout(LayoutKind.Sequential)]
	public struct BackendDx12
	{
		public Header Header;
		public ID3D12Device* Device;
	}

	[StructLayout(LayoutKind.Sequential)]
	public struct ProviderVersion
	{
		public Header Header;
		public ulong VersionId;
		public byte* VersionName;
	}

	[StructLayout(LayoutKind.Sequential)]
	public struct Resource
	{
		public ID3D12Resource* Pointer;
		public uint Type, Format, Width, Height, Depth, MipCount, Flags, Usage;
		public uint State;
	}

	private delegate void MessageDelegate(uint type, char* message);

	// Kept alive here, the DLLs call it from any context
	private static readonly MessageDelegate MessageCallback = OnMessage;

	private static nint loader;
	private static delegate* unmanaged<nint*, Header*, void*, uint> createContext;
	private static delegate* unmanaged<nint*, void*, uint> destroyContext;
	private static delegate* unmanaged<nint*, Header*, uint> configure;
	private static delegate* unmanaged<nint*, Header*, uint> query;
	private static delegate* unmanaged<nint*, Header*, uint> dispatch;

	public static nint Message => Marshal.GetFunctionPointerForDelegate(MessageCallback);

	/// <summary>The effect's DLL by full path, then the loader once</summary>
	public static void Load(string effectDll)
	{
		string directory = Service.PluginInterface.AssemblyLocation.DirectoryName!;
		NativeLibrary.Load(Path.Combine(directory, effectDll));
		if (loader != 0)
			return;

		loader = NativeLibrary.Load(Path.Combine(directory, LoaderDll));
		query = (delegate* unmanaged<nint*, Header*, uint>)NativeLibrary.GetExport(loader, "ffxQuery");
		dispatch = (delegate* unmanaged<nint*, Header*, uint>)NativeLibrary.GetExport(loader, "ffxDispatch");
		configure = (delegate* unmanaged<nint*, Header*, uint>)NativeLibrary.GetExport(loader, "ffxConfigure");
		destroyContext = (delegate* unmanaged<nint*, void*, uint>)NativeLibrary.GetExport(loader, "ffxDestroyContext");
		createContext = (delegate* unmanaged<nint*, Header*, void*, uint>)NativeLibrary.GetExport(loader, "ffxCreateContext");
	}

	public static nint CreateContext(Header* desc, string what)
	{
		nint created;
		Check(createContext(&created, desc, null), what);
		return created;
	}

	public static void Configure(nint context, Header* desc, string what) => Check(configure(&context, desc), what);

	public static void Dispatch(nint context, Header* desc, string what) => Check(dispatch(&context, desc), what);

	public static void Destroy(ref nint context)
	{
		if (context == 0)
			return;

		nint current = context;
		destroyContext(&current, null);
		context = 0;
	}

	/// <summary>"FSR 4.1.1" style name of the provider the loader picked for the context, null when it doesn't say</summary>
	public static string? ProviderName(nint context)
	{
		ProviderVersion provider = new() { Header = { Type = DescQueryProviderVersion } };
		return query(&context, &provider.Header) == 0 && provider.VersionName != null
			? Marshal.PtrToStringAnsi((nint)provider.VersionName)
			: null;
	}

	/// <summary>FFX transitions from and back to the given state itself</summary>
	public static Resource Describe(in Input input) => new()
	{
		Pointer = input.Resource, Type = ResourceTypeTexture2D, Format = Format(input.Format), Width = input.Width, Height = input.Height,
		Depth = 1, MipCount = 1, Usage = input.UnorderedAccess ? ResourceUsageUav : 0, State = ResourceStateCommon,
	};

	/// <summary>FfxApiSurfaceFormat, SDK 2.3</summary>
	public static uint Format(DXGI_FORMAT format) => format switch
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
}
