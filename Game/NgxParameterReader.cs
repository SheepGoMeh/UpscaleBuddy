using System.Collections.Generic;
using System.Text;

namespace UpscaleBuddy.Game;

/// <summary>
/// Reads values out of the game's real NVSDK_NGX_Parameter (the one NGX made, not <see cref="NgxParameters"/>) through its vtable
/// </summary>
internal static unsafe class NgxParameterReader
{
	private const int Success = 1;

	// MSVC reverse overload order: 8 GetVoidPointer, 10 GetD3d11Resource, 11 GetI, 12 GetUI, 13 GetD, 14 GetF
	private const int GetVoidPointer = 8;
	private const int GetResource = 10;
	private const int GetInt = 11;
	private const int GetUInt = 12;
	private const int GetDouble = 13;
	private const int GetFloat = 14;

	private static readonly Dictionary<string, byte[]> Keys = [];

	/// <summary>The value under name, whichever numeric type NGX stored it as</summary>
	public static bool TryNumber(nint parameters, string name, out double value)
	{
		nint* table = *(nint**)parameters;
		fixed (byte* key = Key(name))
		{
			float f;
			if (((delegate* unmanaged<nint, byte*, float*, int>)table[GetFloat])(parameters, key, &f) == Success)
			{
				value = f;
				return true;
			}

			double d;
			if (((delegate* unmanaged<nint, byte*, double*, int>)table[GetDouble])(parameters, key, &d) == Success)
			{
				value = d;
				return true;
			}

			uint u;
			if (((delegate* unmanaged<nint, byte*, uint*, int>)table[GetUInt])(parameters, key, &u) == Success)
			{
				value = u;
				return true;
			}

			int i;
			if (((delegate* unmanaged<nint, byte*, int*, int>)table[GetInt])(parameters, key, &i) == Success)
			{
				value = i;
				return true;
			}
		}

		value = 0;
		return false;
	}

	public static double Number(nint parameters, string name) => TryNumber(parameters, name, out double value) ? value : 0;

	/// <summary>A resource or pointer set under name, 0 when there is none</summary>
	public static nint Pointer(nint parameters, string name)
	{
		nint* table = *(nint**)parameters;
		fixed (byte* key = Key(name))
		{
			nint pointer = 0;
			if (((delegate* unmanaged<nint, byte*, nint*, int>)table[GetResource])(parameters, key, &pointer) == Success && pointer != 0)
				return pointer;

			pointer = 0;
			return ((delegate* unmanaged<nint, byte*, nint*, int>)table[GetVoidPointer])(parameters, key, &pointer) == Success ? pointer : 0;
		}
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
