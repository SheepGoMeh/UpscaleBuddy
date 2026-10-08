using System;
using System.IO;
using System.Runtime.InteropServices;

using TerraFX.Interop.DirectX;

using UpscaleBuddy.D3D12;
using UpscaleBuddy.Fsr3;

using static TerraFX.Interop.DirectX.D3D12_RESOURCE_STATES;

namespace UpscaleBuddy.Xess;

/// <summary>
/// Intel's XeSS super resolution (libxess.dll, XeSS SDK 3.x) through its D3D12 API, which runs on any GPU with DP4a
/// Inputs are expected in NON_PIXEL_SHADER_RESOURCE and the output in UNORDERED_ACCESS, so Record transitions them
/// </summary>
public unsafe class XessBackend: IBackend
{
	private const string Dll = "libxess.dll";

	private const uint InitInvertedDepth = 1 << 1;
	private const uint InitLdrInputColor = 1 << 6;
	private const int LoggingLevelWarning = 2;

	// xessForceLegacyScaleFactors(true): the same per-axis ratios as the plugin's modes
	private static readonly (float Ratio, int Quality)[] QualityByRatio =
	[
		(3.0f, 100), // ULTRA_PERFORMANCE
		(2.0f, 101), // PERFORMANCE
		(1.7f, 102), // BALANCED
		(1.5f, 103), // QUALITY
		(1.3f, 104), // ULTRA_QUALITY
		(1.0f, 106), // AA
	];

	[StructLayout(LayoutKind.Sequential)]
	private struct Version
	{
		public ushort Major, Minor, Patch, Reserved;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct InitParams
	{
		public uint OutputWidth, OutputHeight;
		public int QualitySetting;
		public uint InitFlags;
		public uint CreationNodeMask;
		public uint VisibleNodeMask;
		public void* TempBufferHeap;
		public ulong BufferHeapOffset;
		public void* TempTextureHeap;
		public ulong TextureHeapOffset;
		public void* PipelineLibrary;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct ExecuteParams
	{
		public ID3D12Resource* ColorTexture;
		public ID3D12Resource* VelocityTexture;
		public ID3D12Resource* DepthTexture;
		public ID3D12Resource* ExposureScaleTexture;
		public ID3D12Resource* ResponsivePixelMaskTexture;
		public ID3D12Resource* OutputTexture;
		public float JitterOffsetX;
		public float JitterOffsetY;
		public float ExposureScale;
		public uint ResetHistory;
		public uint InputWidth;
		public uint InputHeight;
		public uint InputColorBaseX, InputColorBaseY;
		public uint InputMotionVectorBaseX, InputMotionVectorBaseY;
		public uint InputDepthBaseX, InputDepthBaseY;
		public uint InputResponsiveMaskBaseX, InputResponsiveMaskBaseY;
		public uint Reserved0X, Reserved0Y;
		public uint OutputColorBaseX, OutputColorBaseY;
		public void* DescriptorHeap;
		public uint DescriptorHeapOffset;
	}

	private delegate void LogDelegate(byte* message, int level);

	// Kept alive here, the DLL calls it from any context
	private static readonly LogDelegate LogCallback = OnLog;

	private static delegate* unmanaged<ID3D12Device*, nint*, int> createContext;
	private static delegate* unmanaged<nint, InitParams*, int> init;
	private static delegate* unmanaged<nint, ID3D12GraphicsCommandList*, ExecuteParams*, int> execute;
	private static delegate* unmanaged<nint, int> destroyContext;
	private static delegate* unmanaged<nint, float, float, int> setVelocityScale;
	private static delegate* unmanaged<nint, byte, int> forceLegacyScaleFactors;
	private static delegate* unmanaged<nint, int, nint, int> setLoggingCallback;
	private static delegate* unmanaged<Version*, int> getVersion;

	private nint context;

	public XessBackend(ID3D12Device* device, uint maxRenderWidth, uint upscaleWidth, uint upscaleHeight)
	{
		LoadLibrary();

		nint created;
		Check(createContext(device, &created), "xessD3D12CreateContext");
		this.context = created;
		try
		{
			Check(setLoggingCallback(created, LoggingLevelWarning, Marshal.GetFunctionPointerForDelegate(LogCallback)), "xessSetLoggingCallback");
			Check(forceLegacyScaleFactors(created, 1), "xessForceLegacyScaleFactors");

			// Same permutation as the other upscalers: LDR, low resolution MVs, inverted depth
			InitParams parameters = new()
			{
				OutputWidth = upscaleWidth, OutputHeight = upscaleHeight, QualitySetting = Quality(upscaleWidth / (float)maxRenderWidth),
				InitFlags = InitInvertedDepth | InitLdrInputColor, CreationNodeMask = 1, VisibleNodeMask = 1,
			};
			Check(init(created, &parameters), "xessD3D12Init");

			Version version;
			this.Name = getVersion(&version) >= 0 ? $"XeSS {version.Major}.{version.Minor}.{version.Patch} (Intel DLL, D3D12)" : "XeSS (Intel DLL, D3D12)";
		}
		catch
		{
			this.Dispose();
			throw;
		}
	}

	public string Name { get; } = "";

	public string Label => "XeSS";

	public void Record(ID3D12GraphicsCommandList* list, in Input color, in Input motionVectors, in Input depth, in Input output,
		in Fsr3Upscaler.DispatchParams p, uint upscaleWidth, uint upscaleHeight)
	{
		// NGX's MV.Scale and jitter use XeSS's conventions as they are
		Check(setVelocityScale(this.context, p.MotionVectorScaleX, p.MotionVectorScaleY), "xessSetVelocityScale");

		Transition(list, color, motionVectors, depth, D3D12_RESOURCE_STATE_COMMON, D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE,
			output, D3D12_RESOURCE_STATE_COMMON, D3D12_RESOURCE_STATE_UNORDERED_ACCESS);

		ExecuteParams parameters = new()
		{
			ColorTexture = color.Resource,
			VelocityTexture = motionVectors.Resource,
			DepthTexture = depth.Resource,
			OutputTexture = output.Resource,
			JitterOffsetX = p.JitterX,
			JitterOffsetY = p.JitterY,
			ExposureScale = 1.0f,
			ResetHistory = p.Reset ? 1u : 0u,
			InputWidth = p.RenderWidth,
			InputHeight = p.RenderHeight,
		};
		Check(execute(this.context, list, &parameters), "xessD3D12Execute");

		Transition(list, color, motionVectors, depth, D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE, D3D12_RESOURCE_STATE_COMMON,
			output, D3D12_RESOURCE_STATE_UNORDERED_ACCESS, D3D12_RESOURCE_STATE_COMMON);
	}

	private static void Transition(ID3D12GraphicsCommandList* list, in Input color, in Input motionVectors, in Input depth,
		D3D12_RESOURCE_STATES inputFrom, D3D12_RESOURCE_STATES inputTo, in Input output, D3D12_RESOURCE_STATES outputFrom,
		D3D12_RESOURCE_STATES outputTo)
	{
		D3D12_RESOURCE_BARRIER* barriers = stackalloc D3D12_RESOURCE_BARRIER[4]
		{
			D3D12_RESOURCE_BARRIER.InitTransition(color.Resource, inputFrom, inputTo),
			D3D12_RESOURCE_BARRIER.InitTransition(motionVectors.Resource, inputFrom, inputTo),
			D3D12_RESOURCE_BARRIER.InitTransition(depth.Resource, inputFrom, inputTo),
			D3D12_RESOURCE_BARRIER.InitTransition(output.Resource, outputFrom, outputTo),
		};
		list->ResourceBarrier(4, barriers);
	}

	/// <summary>The preset whose legacy ratio is closest to the actual one</summary>
	private static int Quality(float ratio)
	{
		(float Ratio, int Quality) best = QualityByRatio[0];
		foreach ((float Ratio, int Quality) entry in QualityByRatio)
		{
			if (MathF.Abs(entry.Ratio - ratio) < MathF.Abs(best.Ratio - ratio))
				best = entry;
		}

		return best.Quality;
	}

	private static void LoadLibrary()
	{
		if (createContext != null)
			return;

		nint library = NativeLibrary.Load(Path.Combine(Service.PluginInterface.AssemblyLocation.DirectoryName!, Dll));
		init = (delegate* unmanaged<nint, InitParams*, int>)NativeLibrary.GetExport(library, "xessD3D12Init");
		execute = (delegate* unmanaged<nint, ID3D12GraphicsCommandList*, ExecuteParams*, int>)NativeLibrary.GetExport(library, "xessD3D12Execute");
		destroyContext = (delegate* unmanaged<nint, int>)NativeLibrary.GetExport(library, "xessDestroyContext");
		setVelocityScale = (delegate* unmanaged<nint, float, float, int>)NativeLibrary.GetExport(library, "xessSetVelocityScale");
		forceLegacyScaleFactors = (delegate* unmanaged<nint, byte, int>)NativeLibrary.GetExport(library, "xessForceLegacyScaleFactors");
		setLoggingCallback = (delegate* unmanaged<nint, int, nint, int>)NativeLibrary.GetExport(library, "xessSetLoggingCallback");
		getVersion = (delegate* unmanaged<Version*, int>)NativeLibrary.GetExport(library, "xessGetVersion");
		createContext = (delegate* unmanaged<ID3D12Device*, nint*, int>)NativeLibrary.GetExport(library, "xessD3D12CreateContext");
	}

	private static void OnLog(byte* message, int level)
	{
		string text = Marshal.PtrToStringUTF8((nint)message) ?? "";
		if (level > LoggingLevelWarning)
			Service.PluginLog.Error($"XeSS: {text}");
		else
			Service.PluginLog.Warning($"XeSS: {text}");
	}

	/// <summary>Negative results are errors, positive ones warnings</summary>
	private static void Check(int result, string what)
	{
		if (result < 0)
			throw new InvalidOperationException($"{what} failed: {result}");
	}

	/// <summary>D3D12Upscaler waits for the queue first</summary>
	public void Dispose()
	{
		if (this.context != 0)
		{
			destroyContext(this.context);
			this.context = 0;
		}

		GC.SuppressFinalize(this);
	}
}
