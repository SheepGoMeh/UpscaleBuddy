using System;
using System.IO;
using System.Runtime.InteropServices;

using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;

using static TerraFX.Interop.DirectX.D3D_FEATURE_LEVEL;
using static TerraFX.Interop.Windows.Windows;

namespace UpscaleBuddy.D3D12;

/// <summary>
/// A D3D12 device under Wine/Proton, for the D3D11 game. Wine picks its builtin d3d12 by file name, which makes no device unless the
/// launcher overrides d3d12 and d3d12core, and a full path doesn't change that. So this loads a copy of the prefix's vkd3d-proton core
/// under another name, which Wine takes as the native library it is, and creates the device through the core's interface, as
/// vkd3d-proton's own d3d12.dll does
/// </summary>
public static unsafe class WineVkd3d
{
	public static readonly bool IsWine = NativeLibrary.TryLoad("ntdll.dll", out nint ntdll) && NativeLibrary.TryGetExport(ntdll, "wine_get_version", out _);

	// Loaded once, the core stays loaded and hands out one interface. Zero when it can't be used
	private static readonly Lazy<nint> Core = new(LoadCore);

	public static HRESULT CreateDevice(IUnknown* adapter, ID3D12Device** device)
	{
		void* core = (void*)Core.Value;
		if (core == null)
			return E.E_NOINTERFACE;

		// IVKD3DCoreInterface has no IUnknown methods, its vtable starts with CreateDevice
		void** vtable = *(void***)core;
		Guid deviceId = __uuidof<ID3D12Device>();
		HRESULT result = ((delegate* unmanaged<void*, IUnknown*, D3D_FEATURE_LEVEL, Guid*, void**, HRESULT>)vtable[0])(core, adapter, D3D_FEATURE_LEVEL_11_0, &deviceId, (void**)device);
		if (result.FAILED)
			Service.PluginLog.Warning($"vkd3d-proton refused the D3D12 device (0x{result.Value:X8}), the AMD DLL path is unavailable");
		else
			CheckFsr4(*device);

		return result;
	}

	/// <summary>
	/// AMD's FSR 4 providers ask the device for ID3D12DeviceExt3 (the AGS extension), which older vkd3d-proton builds don't have, and
	/// the DLL then only offers FSR 3.1. A prefix keeps the vkd3d-proton of the Proton that created it
	/// </summary>
	private static void CheckFsr4(ID3D12Device* device)
	{
		Guid deviceExt3 = new("3aefa75c-c9f3-4b7f-a180-5df695d083bf");
		void* extension;
		if (((IUnknown*)device)->QueryInterface(&deviceExt3, &extension).SUCCEEDED)
			((IUnknown*)extension)->Release();
		else
			Service.PluginLog.Warning("The prefix's vkd3d-proton is too old for FSR 4 (no ID3D12DeviceExt3), the AMD DLL path stays on FSR 3.1");
	}

	private static nint LoadCore()
	{
		string source = Path.Combine(Environment.SystemDirectory, "d3d12core.dll");
		string copy = Path.Combine(Path.GetTempPath(), "UpscaleBuddy", "vkd3d_proton_core.dll");
		try
		{
			// Wine's builtin is a small placeholder, vkd3d-proton's core is megabytes
			FileInfo info = new(source);
			if (!info.Exists || info.Length < 1_000_000)
			{
				Service.PluginLog.Warning($"The prefix has no vkd3d-proton core in {source}, the AMD DLL path is unavailable");
				return 0;
			}

			Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
			FileInfo existing = new(copy);
			if (existing.Length != info.Length || existing.LastWriteTimeUtc != info.LastWriteTimeUtc)
				File.Copy(source, copy, true);
		}
		catch (Exception e)
		{
			Service.PluginLog.Warning(e, "Copying the prefix's vkd3d-proton core failed, the AMD DLL path is unavailable");
			return 0;
		}

		if (!NativeLibrary.TryLoad(copy, out nint module) || !NativeLibrary.TryGetExport(module, "D3D12GetInterface", out nint getInterface))
		{
			Service.PluginLog.Warning($"Loading {copy} failed, the AMD DLL path is unavailable");
			return 0;
		}

		Guid coreClass = new("ed53efad-da21-4d96-a1bc-e734e078879c"); // CLSID_VKD3DCore
		Guid coreId = new("18da885c-a7b5-464e-a121-ccb75d4dfc31"); // IID_IVKD3DCoreInterface
		void* core;
		HRESULT result = ((delegate* unmanaged<Guid*, Guid*, void**, HRESULT>)getInterface)(&coreClass, &coreId, &core);
		if (result.FAILED)
		{
			Service.PluginLog.Warning($"vkd3d-proton's core has no interface (0x{result.Value:X8}), the AMD DLL path is unavailable");
			return 0;
		}

		return (nint)core;
	}
}
