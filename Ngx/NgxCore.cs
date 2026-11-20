using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;

using Microsoft.Win32;

using TerraFX.Interop.DirectX;

using LUID = TerraFX.Interop.Windows.LUID;

namespace UpscaleBuddy.Ngx;

/// <summary>
/// Finds and loads the driver's NGX core the way nvsdk_ngx_s.lib does:
/// 1. The NGXCore\NGXPath value in the driver service key of the adapter, through D3DKMTQueryAdapterInfo (KMTQAITYPE_QUERYREGISTRY).
/// 2. The NGXCore registry values under HKLM.
/// 3. _nvngx.dll (or nvngx.dll) in that folder, only with a valid signature from NVIDIA Corporation.
/// </summary>
public static unsafe class NgxCore
{
	private const int QueryRegistry = 48; // KMTQAITYPE_QUERYREGISTRY
	private const uint NvidiaVendorId = 0x10DE;

	private static readonly object ResolverGate = new();
	private static nint boundModule;
	private const uint RegSz = 1;
	private const string Signer = "NVIDIA Corporation";

	private static readonly string[] AdapterValues = [@"Parameters\NGXCore\NGXPath", @"NGXCore\NGXPath"];

	private static readonly (string Key, string Value)[] RegistryValues =
	[
		(@"System\CurrentControlSet\Services\nvlddmkm\Parameters\NGXCore", "NGXPath"),
		(@"System\CurrentControlSet\Services\nvlddmkm\NGXCore", "NGXPath"),
		(@"SOFTWARE\NVIDIA Corporation\Global\NGXCore", "FullPath"),
	];

	/// <summary>D3DDDI_QUERYREGISTRY_INFO</summary>
	[StructLayout(LayoutKind.Sequential)]
	public struct QueryRegistryInfo
	{
		public int QueryType; // 0 = D3DDDI_QUERYREGISTRY_SERVICEKEY
		public uint QueryFlags;
		public fixed char ValueName[260];
		public uint ValueType;
		public uint PhysicalAdapterIndex;
		public uint OutputValueSize;
		public int Status; // 0 = success
		public fixed char Output[264];
	}

	/// <summary>NVSDK_NGX_FeatureCommonInfo</summary>
	[StructLayout(LayoutKind.Sequential)]
	public struct FeatureCommonInfo
	{
		public char** Paths;
		public uint PathCount;
		public void* InternalData;
		public nint LoggingCallback;
		public int MinimumLoggingLevel;
		public byte DisableOtherLoggingSinks;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct AdapterInfo
	{
		public uint Adapter;
		public LUID Luid;
		public uint SourceCount;
		public int PrecisePresentRegionsPreferred;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct EnumAdapters2
	{
		public uint Count;
		public AdapterInfo* Adapters;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct QueryAdapterInfo
	{
		public uint Adapter;
		public int Type;
		public void* PrivateDriverData;
		public uint PrivateDriverDataSize;
	}

	[DllImport("gdi32.dll")]
	private static extern int D3DKMTEnumAdapters2(EnumAdapters2* adapters);

	[DllImport("gdi32.dll")]
	private static extern int D3DKMTQueryAdapterInfo(QueryAdapterInfo* query);

	[DllImport("gdi32.dll")]
	private static extern int D3DKMTCloseAdapter(uint* adapter);

	[DllImport("wintrust.dll", CharSet = CharSet.Unicode)]
	private static extern int WinVerifyTrust(nint window, Guid* action, WinTrustData* data);

	[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
	private struct WinTrustFileInfo
	{
		public uint Size;
		public char* Path;
		public nint File;
		public Guid* KnownSubject;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct WinTrustData
	{
		public uint Size;
		public nint PolicyCallbackData, SipClientData;
		public uint UiChoice, RevocationChecks, UnionChoice;
		public WinTrustFileInfo* File;
		public uint StateAction;
		public nint StateData;
		public char* UrlReference;
		public uint ProviderFlags, UiContext;
		public nint SignatureSettings;
	}


	/// <summary>Returns the folder that holds the NGX core for the adapter, or null. On null, reason gives the cause.</summary>
	public static string? FindCoreFolder(LUID adapter, out string reason)
	{
		List<string> misses = [];
		foreach (string value in AdapterValues)
		{
			string? folder = QueryAdapterRegistry(adapter, value, out string miss);
			if (folder != null && Directory.Exists(folder))
			{
				reason = "";
				return folder;
			}

			misses.Add($"adapter {value}: {miss}");
		}

		foreach ((string key, string value) in RegistryValues)
		{
			string? folder = ReadRegistry(key, value);
			if (folder != null && Directory.Exists(folder))
			{
				reason = "";
				return folder;
			}

			misses.Add($"HKLM\\{key} {value}: {(folder == null ? "absent" : "not a folder")}");
		}

		reason = $"no NGX core folder ({string.Join("; ", misses)})";
		return null;
	}

	/// <summary>Loads the NGX core of the adapter and gets its D3D12 exports. Returns false with a reason.</summary>
	public static bool TryLoad(LUID adapter, out INgxCore? core, out string reason)
	{
		core = null;
		uint vendor = VendorId(adapter);
		if (vendor != NvidiaVendorId)
		{
			reason = $"not an NVIDIA adapter (vendor 0x{vendor:X4})";
			return false;
		}

		string? folder = FindCoreFolder(adapter, out reason);
		if (folder == null)
			return false;

		foreach (string file in new[] { "_nvngx.dll", "nvngx.dll" })
		{
			string path = Path.Combine(folder, file);
			if (!File.Exists(path))
				continue;
			if (!SignedByNvidia(path))
			{
				reason = $"{path} has no valid signature from {Signer}";
				return false;
			}

			if (!NativeLibrary.TryLoad(path, out nint module))
			{
				reason = $"{path} did not load (error {Marshal.GetLastPInvokeError()})";
				return false;
			}

			return TryBind(module, out core, out reason);
		}

		reason = $"no _nvngx.dll or nvngx.dll in {folder}";
		return false;
	}

	/// <summary>True when the file has a valid embedded Authenticode signature whose signer is NVIDIA Corporation.</summary>
	public static bool SignedByNvidia(string path)
	{
		if (!File.Exists(path))
			return false;

		Guid action = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE"); // WINTRUST_ACTION_GENERIC_VERIFY_V2
		int status;
		fixed (char* file = path)
		{
			WinTrustFileInfo info = new() { Size = (uint)sizeof(WinTrustFileInfo), Path = file };
			WinTrustData data = new()
			{
				Size = (uint)sizeof(WinTrustData),
				UiChoice = 2, // WTD_UI_NONE
				RevocationChecks = 0, // WTD_REVOKE_NONE
				UnionChoice = 1, // WTD_CHOICE_FILE
				File = &info,
				StateAction = 1, // WTD_STATEACTION_VERIFY
			};
			status = WinVerifyTrust(-1, &action, &data); // INVALID_HANDLE_VALUE: no UI
			data.StateAction = 2; // WTD_STATEACTION_CLOSE
			WinVerifyTrust(-1, &action, &data);
		}

		if (status != 0)
			return false;

		try
		{
#pragma warning disable SYSLIB0057 // Reads the signer of an Authenticode signature, not a certificate file
			using X509Certificate certificate = X509Certificate.CreateFromSignedFile(path);
#pragma warning restore SYSLIB0057
			using X509Certificate2 signer = new(certificate);
			return signer.GetNameInfo(X509NameType.SimpleName, false) == Signer;
		}
		catch (Exception)
		{
			return false;
		}
	}

	/// <summary>Returns the PCI vendor ID of the adapter, or 0 when DXGI does not find it.</summary>
	private static uint VendorId(LUID luid)
	{
		IDXGIFactory4* factory;
		if (DirectX.CreateDXGIFactory1(TerraFX.Interop.Windows.Windows.__uuidof<IDXGIFactory4>(), (void**)&factory) < 0)
			return 0;
		IDXGIAdapter1* adapter;
		uint vendor = 0;
		if (factory->EnumAdapterByLuid(luid, TerraFX.Interop.Windows.Windows.__uuidof<IDXGIAdapter1>(), (void**)&adapter) >= 0)
		{
			DXGI_ADAPTER_DESC1 desc;
			if (adapter->GetDesc1(&desc) >= 0)
				vendor = desc.VendorId;
			adapter->Release();
		}

		factory->Release();
		return vendor;
	}

	/// <summary>Binds DriverNgxCore.Library to module when module has every export that DriverNgxCore calls.</summary>
	private static bool TryBind(nint module, out INgxCore? core, out string reason)
	{
		List<string> missing = DriverNgxCore.Exports.Where(name => !NativeLibrary.TryGetExport(module, name, out _)).ToList();
		if (missing.Count > 0)
		{
			core = null;
			reason = $"the NGX core has no {string.Join(", ", missing)}";
			return false;
		}

		lock (ResolverGate)
		{
			if (boundModule == 0)
				NativeLibrary.SetDllImportResolver(typeof(NgxCore).Assembly, Resolve);
			boundModule = module;
		}

		core = new DriverNgxCore();
		reason = "";
		return true;
	}

	private static nint Resolve(string name, Assembly assembly, DllImportSearchPath? searchPath) =>
		name == DriverNgxCore.Library ? boundModule : 0;

	private static string? QueryAdapterRegistry(LUID luid, string valueName, out string miss)
	{
		EnumAdapters2 enumeration = default;
		if (D3DKMTEnumAdapters2(&enumeration) != 0 || enumeration.Count == 0)
		{
			miss = "D3DKMTEnumAdapters2 failed";
			return null;
		}

		AdapterInfo[] adapters = new AdapterInfo[enumeration.Count];
		fixed (AdapterInfo* list = adapters)
		{
			enumeration.Adapters = list;
			if (D3DKMTEnumAdapters2(&enumeration) != 0)
			{
				miss = "D3DKMTEnumAdapters2 failed";
				return null;
			}
		}

		string? result = null;
		miss = "no adapter with this LUID";
		for (int i = 0; i < enumeration.Count; i++)
		{
			AdapterInfo adapter = adapters[i];
			if (result == null && adapter.Luid.LowPart == luid.LowPart && adapter.Luid.HighPart == luid.HighPart)
				result = QueryValue(adapter.Adapter, valueName, out miss);
			D3DKMTCloseAdapter(&adapter.Adapter);
		}

		return result;
	}

	private static string? QueryValue(uint adapter, string valueName, out string miss)
	{
		QueryRegistryInfo info = new() { QueryType = 0, QueryFlags = 0, ValueType = RegSz };
		valueName.AsSpan().CopyTo(new Span<char>(info.ValueName, 259));
		QueryAdapterInfo query = new()
		{
			Adapter = adapter, Type = QueryRegistry, PrivateDriverData = &info, PrivateDriverDataSize = (uint)sizeof(QueryRegistryInfo),
		};
		int status = D3DKMTQueryAdapterInfo(&query);
		if (status < 0 || info.Status != 0)
		{
			miss = $"status 0x{status:X8}, registry status {info.Status}";
			return null;
		}

		miss = "";
		return new string(info.Output);
	}

	private static string? ReadRegistry(string key, string value)
	{
		using RegistryKey root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
		using RegistryKey? subKey = root.OpenSubKey(key);
		return subKey?.GetValue(value) as string;
	}
}
