using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;

using static TerraFX.Interop.DirectX.D3D_FEATURE_LEVEL;
using static TerraFX.Interop.Windows.Windows;

namespace UpscaleBuddy.D3D12;

/// <summary>
/// D3D12 devices from the vkd3d-proton of a Wine prefix that does not use vkd3d-proton for d3d12.dll.
/// Requires vkd3d-proton 3.0.1 or later, for ID3D12DeviceFactory.
/// Wine selects its builtin DLL or the native DLL by the base name, also for a full path. Thus the copy has a different name.
/// </summary>
public static unsafe class WineVkd3d
{
	private const string CopyName = "vkd3d_proton_d3d12core.dll";

	// Wine marks its builtin PE files with this string after the DOS header
	private static readonly byte[] BuiltinSignature = Encoding.ASCII.GetBytes("Wine builtin DLL");

	private static readonly Guid DeviceFactoryClass = new("114863bf-c386-4aee-b39d-8f0bbb062955"); // CLSID_D3D12DeviceFactory
	private static readonly Guid DeviceExt3 = new("3aefa75c-c9f3-4b7f-a180-5df695d083bf"); // The vkd3d-proton interface for the AMD AGS extensions

	// Zero when the prefix has no usable vkd3d-proton
	private static readonly Lazy<nint> GetInterface = new(LoadCore);

	public static HRESULT CreateDevice(IUnknown* adapter, ID3D12Device** device)
	{
		var getInterface = (delegate* unmanaged<Guid*, Guid*, void**, HRESULT>)GetInterface.Value;
		if (getInterface == null)
			return E.E_NOINTERFACE;

		Guid factoryClass = DeviceFactoryClass;
		ID3D12DeviceFactory* factory;
		HRESULT result = getInterface(&factoryClass, __uuidof<ID3D12DeviceFactory>(), (void**)&factory);
		if (result.FAILED)
		{
			Service.PluginLog.Warning($"vkd3d-proton has no device factory (0x{result.Value:X8}), it needs 3.0.1 or later for the D3D12 libraries");
			return result;
		}

		result = factory->CreateDevice(adapter, D3D_FEATURE_LEVEL_11_0, __uuidof<ID3D12Device>(), (void**)device);
		factory->Release();
		if (result.FAILED)
			Service.PluginLog.Warning($"vkd3d-proton refused the D3D12 device (0x{result.Value:X8})");
		else
			CheckFsr4(*device);

		return result;
	}

	/// <summary>FSR 4 in the AMD DLL requires ID3D12DeviceExt3</summary>
	private static void CheckFsr4(ID3D12Device* device)
	{
		Guid id = DeviceExt3;
		void* extension;
		if (((IUnknown*)device)->QueryInterface(&id, &extension).SUCCEEDED)
			((IUnknown*)extension)->Release();
		else
			Service.PluginLog.Warning("The prefix's vkd3d-proton has no ID3D12DeviceExt3, the AMD DLL stays on FSR 3.1");
	}

	private static nint LoadCore()
	{
		string source = Path.Combine(Environment.SystemDirectory, "d3d12core.dll");
		string copy = Path.Combine(Service.PluginInterface.ConfigDirectory.FullName, CopyName);
		try
		{
			if (!File.Exists(source) || IsWineBuiltin(source))
			{
				Service.PluginLog.Warning($"{source} isn't vkd3d-proton, the D3D12 libraries are unavailable under this Wine setup");
				return 0;
			}

			FileInfo info = new(source);
			FileInfo existing = new(copy);
			if (!existing.Exists || existing.Length != info.Length || existing.LastWriteTimeUtc != info.LastWriteTimeUtc)
			{
				Directory.CreateDirectory(existing.DirectoryName!);
				File.Copy(source, copy, true);
			}
		}
		catch (Exception e)
		{
			Service.PluginLog.Warning(e, $"Copying {source} failed, the D3D12 libraries are unavailable");
			return 0;
		}

		if (!NativeLibrary.TryLoad(copy, out nint module) || !NativeLibrary.TryGetExport(module, "D3D12GetInterface", out nint getInterface))
		{
			Service.PluginLog.Warning($"{copy} has no D3D12GetInterface, the D3D12 libraries are unavailable");
			return 0;
		}

		return getInterface;
	}

	private static bool IsWineBuiltin(string path)
	{
		Span<byte> header = stackalloc byte[sizeof(IMAGE_DOS_HEADER) + 16];
		using FileStream stream = File.OpenRead(path);
		return stream.ReadAtLeast(header, header.Length, false) == header.Length &&
		       header[sizeof(IMAGE_DOS_HEADER)..].SequenceEqual(BuiltinSignature);
	}
}
