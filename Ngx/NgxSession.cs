using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

using TerraFX.Interop.DirectX;

namespace UpscaleBuddy.Ngx;

/// <summary>
/// The process's one NGX D3D12 session. D3D12 gives one device for each adapter, so the sync and the async bridge share
/// it. The session holds a reference on the device and stays open until Shutdown.
/// </summary>
public sealed unsafe class NgxSession
{
	private const int SdkVersion = 0x15;
	private const int EngineTypeCustom = 0;
	private const string ProjectId = "6e1d3c3e-6a57-4f2b-9d6e-2b8a7c41d0f5";
	private const string EngineVersion = "1.0";
#if DEBUG
	private const int LoggingLevel = 2; // NVSDK_NGX_LOGGING_LEVEL_VERBOSE
#else
	private const int LoggingLevel = 0; // NVSDK_NGX_LOGGING_LEVEL_OFF
#endif

	private static readonly Lock Gate = new();
	private static NgxSession? current;

	private readonly ID3D12Device* device;
	private readonly byte* projectId, engineVersion;
	private readonly char* dataPath;
	private readonly void* internalData;

	// Not null when NGX opened but DLSS frame generation is not available. The session then stays open until Shutdown.
	private string? unavailable;

	private NgxSession(ID3D12Device* device, INgxCore core, string dataPath)
	{
		this.device = device;
		this.Core = core;
		this.projectId = Utf8(ProjectId);
		this.engineVersion = Utf8(EngineVersion);
		this.dataPath = (char*)Marshal.StringToHGlobalUni(dataPath);
		this.internalData = NativeMemory.AllocZeroed(16);
	}

	/// <summary>When set, Get uses this core and does not load the driver's NGX core.</summary>
	internal static INgxCore? TestCore { get; set; }

	/// <summary>Receives the probe values of DLSS frame generation, once for each session.</summary>
	public static Action<string>? Log { get; set; }

	public INgxCore Core { get; }

	/// <summary>
	/// Returns the session on device, and opens it on the first call. Returns null with a reason when the NGX core is
	/// absent, init fails or DLSS frame generation is not available.
	/// </summary>
	public static NgxSession? Get(ID3D12Device* device, string dataPath, out string reason)
	{
		lock (Gate)
		{
			reason = "";
			if (current != null && current.device == device)
			{
				reason = current.unavailable ?? "";
				return current.unavailable == null ? current : null;
			}

			ShutdownLocked();

			INgxCore? core = TestCore;
			if (core == null && !NgxCore.TryLoad(device->GetAdapterLuid(), out core, out reason))
				return null;

			NgxSession session = new(device, core!, dataPath);
			bool available = session.Open(out reason, out bool opened);
			if (!opened)
			{
				session.Free();
				return null;
			}

			// Only Shutdown calls Shutdown1: the game's own NGX session can be live
			session.unavailable = available ? null : reason;
			device->AddRef();
			current = session;
			return available ? session : null;
		}
	}

	/// <summary>Closes the session. Call it after you release every feature of the session.</summary>
	public static void Shutdown()
	{
		lock (Gate)
			ShutdownLocked();
	}

	private static void ShutdownLocked()
	{
		if (current == null)
			return;

		current.Core.Shutdown1(current.device);
		current.device->Release();
		current.Free();
		current = null;
	}

	/// <summary>Returns true when DLSS frame generation is available. opened is true when Init succeeded.</summary>
	private bool Open(out string reason, out bool opened)
	{
		NgxCore.FeatureCommonInfo info = new() { InternalData = this.internalData, MinimumLoggingLevel = LoggingLevel };
		int result = this.Core.InitProjectId(this.projectId, EngineTypeCustom, this.engineVersion, this.dataPath, this.device, SdkVersion, &info);
		opened = !NgxException.Failed(result);
		if (!opened)
		{
			reason = $"NGX Init_ProjectID: {NgxException.Name(result)}";
			return false;
		}

		NgxParameter* capabilities = null;
		result = this.Core.GetCapabilityParameters(&capabilities);
		if (NgxException.Failed(result) || capabilities == null)
		{
			reason = $"NGX GetCapabilityParameters: {NgxException.Name(result)}";
			return false;
		}

		capabilities->TryGetInt("FrameGeneration.Available", out int available);
		capabilities->TryGetInt("FrameGeneration.FeatureInitResult", out int initResult);
		capabilities->TryGetUInt("FrameGeneration.NeedsUpdatedDriver", out uint needsDriver);
		capabilities->TryGetUInt("FrameGeneration.MinDriverVersionMajor", out uint major);
		capabilities->TryGetUInt("FrameGeneration.MinDriverVersionMinor", out uint minor);
		capabilities->TryGetUInt("DLSSG.MultiFrameCountMax", out uint countMax);
		this.Core.DestroyParameters(capabilities);
		string probe = $"FrameGeneration.Available {available}, FeatureInitResult {NgxException.Name(initResult)}, " +
			$"NeedsUpdatedDriver {needsDriver}, MinDriverVersion {major}.{minor}, MultiFrameCountMax {countMax}";
		Log?.Invoke($"NGX {probe}");
		if (available == 0)
		{
			reason = $"DLSS frame generation is not available: {probe}";
			return false;
		}

		reason = "";
		return true;
	}

	private void Free()
	{
		NativeMemory.Free(this.projectId);
		NativeMemory.Free(this.engineVersion);
		Marshal.FreeHGlobal((nint)this.dataPath);
		NativeMemory.Free(this.internalData);
	}

	private static byte* Utf8(string text)
	{
		byte[] bytes = Encoding.UTF8.GetBytes(text + '\0');
		byte* block = (byte*)NativeMemory.Alloc((nuint)bytes.Length);
		bytes.CopyTo(new Span<byte>(block, bytes.Length));
		return block;
	}
}
