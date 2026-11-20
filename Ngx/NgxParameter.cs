using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text;

using TerraFX.Interop.DirectX;

namespace UpscaleBuddy.Ngx;

/// <summary>NVSDK_NGX_Parameter, an object that NGX makes. The vtable fields follow MSVC's reverse overload order.</summary>
public unsafe struct NgxParameter
{
	private const int Success = 1;

	private static readonly Dictionary<string, byte[]> Keys = [];

	public Vtbl* lpVtbl;

	public struct Vtbl
	{
		public delegate* unmanaged<NgxParameter*, byte*, void*, void> SetVoidPointer;
		public delegate* unmanaged<NgxParameter*, byte*, ID3D12Resource*, void> SetD3d12Resource;
		public delegate* unmanaged<NgxParameter*, byte*, void*, void> SetD3d11Resource;
		public delegate* unmanaged<NgxParameter*, byte*, int, void> SetI;
		public delegate* unmanaged<NgxParameter*, byte*, uint, void> SetUI;
		public delegate* unmanaged<NgxParameter*, byte*, double, void> SetD;
		public delegate* unmanaged<NgxParameter*, byte*, float, void> SetF;
		public delegate* unmanaged<NgxParameter*, byte*, ulong, void> SetULL;
		public delegate* unmanaged<NgxParameter*, byte*, void**, int> GetVoidPointer;
		public delegate* unmanaged<NgxParameter*, byte*, ID3D12Resource**, int> GetD3d12Resource;
		public delegate* unmanaged<NgxParameter*, byte*, void**, int> GetD3d11Resource;
		public delegate* unmanaged<NgxParameter*, byte*, int*, int> GetI;
		public delegate* unmanaged<NgxParameter*, byte*, uint*, int> GetUI;
		public delegate* unmanaged<NgxParameter*, byte*, double*, int> GetD;
		public delegate* unmanaged<NgxParameter*, byte*, float*, int> GetF;
		public delegate* unmanaged<NgxParameter*, byte*, ulong*, int> GetULL;
		public delegate* unmanaged<NgxParameter*, void> Reset;
	}

	private NgxParameter* Self => (NgxParameter*)Unsafe.AsPointer(ref this);

	public void SetUInt(string name, uint value)
	{
		fixed (byte* key = Key(name))
			this.lpVtbl->SetUI(this.Self, key, value);
	}

	public void SetFloat(string name, float value)
	{
		fixed (byte* key = Key(name))
			this.lpVtbl->SetF(this.Self, key, value);
	}

	public void SetPointer(string name, void* value)
	{
		fixed (byte* key = Key(name))
			this.lpVtbl->SetVoidPointer(this.Self, key, value);
	}

	public void SetResource(string name, ID3D12Resource* value)
	{
		fixed (byte* key = Key(name))
			this.lpVtbl->SetD3d12Resource(this.Self, key, value);
	}

	/// <summary>Gets a value that NGX stored as an unsigned int.</summary>
	public bool TryGetUInt(string name, out uint value)
	{
		uint result = 0;
		bool found;
		fixed (byte* key = Key(name))
			found = this.lpVtbl->GetUI(this.Self, key, &result) == Success;
		value = result;
		return found;
	}

	/// <summary>Gets a value that NGX stored as an int.</summary>
	public bool TryGetInt(string name, out int value)
	{
		int result = 0;
		bool found;
		fixed (byte* key = Key(name))
			found = this.lpVtbl->GetI(this.Self, key, &result) == Success;
		value = result;
		return found;
	}

	private static byte[] Key(string name)
	{
		lock (Keys)
		{
			if (!Keys.TryGetValue(name, out byte[]? key))
				Keys[name] = key = Encoding.ASCII.GetBytes(name + '\0');
			return key;
		}
	}
}
