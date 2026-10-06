using System;
using System.Runtime.InteropServices;

namespace UpscaleBuddy.Fsr3;

/// <summary>
/// ID3D11Device / ID3D11DeviceContext calls through the vtables
/// </summary>
public static unsafe class D3D11
{
	public const uint FormatR32G32B32A32Float = 2;
	public const uint FormatR16G16B16A16Float = 10;
	public const uint FormatR32G32Float = 16;
	public const uint FormatR8G8B8A8Unorm = 28;
	public const uint FormatR16G16Float = 34;
	public const uint FormatR32Float = 41;
	public const uint FormatR32Uint = 42;
	public const uint FormatR24UnormX8Typeless = 46;
	public const uint FormatR16Float = 54;
	public const uint FormatR16Snorm = 58;
	public const uint FormatR8Unorm = 61;

	public const uint BindShaderResource = 0x8;
	public const uint BindConstantBuffer = 0x4;
	public const uint BindUnorderedAccess = 0x80;

	[StructLayout(LayoutKind.Sequential)]
	public struct Texture2DDesc
	{
		public uint Width;
		public uint Height;
		public uint MipLevels;
		public uint ArraySize;
		public uint Format;
		public uint SampleCount;
		public uint SampleQuality;
		public uint Usage;
		public uint BindFlags;
		public uint CpuAccessFlags;
		public uint MiscFlags;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct SubresourceData
	{
		public void* SysMem;
		public uint Pitch;
		public uint SlicePitch;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct BufferDesc
	{
		public uint ByteWidth;
		public uint Usage;
		public uint BindFlags;
		public uint CpuAccessFlags;
		public uint MiscFlags;
		public uint StructureByteStride;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct ViewDesc
	{
		public uint Format;
		public uint Dimension;
		public uint A;
		public uint B;
		public uint C;
		public uint D;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct MappedSubresource
	{
		public void* Data;
		public uint RowPitch;
		public uint DepthPitch;
	}

	private static nint Slot(nint obj, int index) => (*(nint**)obj)[index];

	private static void Check(int hr, string what)
	{
		if (hr < 0)
			throw new InvalidOperationException($"{what} failed: 0x{hr:X8}");
	}

	public static void Release(nint obj)
	{
		if (obj != 0)
			((delegate* unmanaged<nint, uint>)Slot(obj, 2))(obj);
	}

	public static nint GetDevice(nint deviceChild)
	{
		nint device;
		((delegate* unmanaged<nint, nint*, void>)Slot(deviceChild, 3))(deviceChild, &device);
		return device;
	}

	public static Texture2DDesc GetDesc(nint texture)
	{
		Texture2DDesc desc;
		((delegate* unmanaged<nint, Texture2DDesc*, void>)Slot(texture, 10))(texture, &desc);
		return desc;
	}

	public static nint CreateTexture(nint device, uint width, uint height, uint format, uint mips, uint bind, void* data = null, uint pitch = 0)
	{
		Texture2DDesc desc = new()
		{
			Width = Math.Max(1, width), Height = Math.Max(1, height), MipLevels = mips, ArraySize = 1, Format = format,
			SampleCount = 1, BindFlags = bind,
		};
		SubresourceData init = new() { SysMem = data, Pitch = pitch };
		nint texture;
		Check(((delegate* unmanaged<nint, Texture2DDesc*, SubresourceData*, nint*, int>)Slot(device, 5))(
			device, &desc, data == null ? null : &init, &texture), "CreateTexture2D");
		return texture;
	}

	public static nint CreateConstantBuffer(nint device, uint size)
	{
		BufferDesc desc = new()
		{
			ByteWidth = (size + 15) & ~15u, Usage = 2 /* DYNAMIC */, BindFlags = BindConstantBuffer, CpuAccessFlags = 0x10000 /* WRITE */,
		};
		nint buffer;
		Check(((delegate* unmanaged<nint, BufferDesc*, void*, nint*, int>)Slot(device, 3))(device, &desc, null, &buffer), "CreateBuffer");
		return buffer;
	}

	/// <summary>SRV over all mips, format 0 uses the texture's format</summary>
	public static nint CreateSrv(nint device, nint texture, uint format = 0)
	{
		ViewDesc desc = new() { Format = format, Dimension = 4 /* TEXTURE2D */, A = 0, B = uint.MaxValue };
		nint view;
		Check(((delegate* unmanaged<nint, nint, ViewDesc*, nint*, int>)Slot(device, 7))(
			device, texture, format == 0 ? null : &desc, &view), "CreateShaderResourceView");
		return view;
	}

	public static nint CreateUav(nint device, nint texture, uint format, uint mip = 0)
	{
		ViewDesc desc = new() { Format = format, Dimension = 4 /* TEXTURE2D */, A = mip };
		nint view;
		Check(((delegate* unmanaged<nint, nint, ViewDesc*, nint*, int>)Slot(device, 8))(device, texture, &desc, &view), "CreateUnorderedAccessView");
		return view;
	}

	public static nint CreateComputeShader(nint device, byte[] bytecode)
	{
		nint shader;
		fixed (byte* code = bytecode)
			Check(((delegate* unmanaged<nint, byte*, nuint, nint, nint*, int>)Slot(device, 18))(
				device, code, (nuint)bytecode.Length, 0, &shader), "CreateComputeShader");
		return shader;
	}

	public static nint CreateSampler(nint device, bool linear)
	{
		uint* desc = stackalloc uint[13];
		desc[0] = linear ? 0x15u : 0u; // MIN_MAG_MIP_LINEAR / POINT
		desc[1] = desc[2] = desc[3] = 3; // CLAMP
		desc[5] = 1; // MaxAnisotropy
		desc[6] = 1; // NEVER
		*(float*)&desc[12] = float.MaxValue; // MaxLOD
		nint sampler;
		Check(((delegate* unmanaged<nint, uint*, nint*, int>)Slot(device, 23))(device, desc, &sampler), "CreateSamplerState");
		return sampler;
	}

	public static void Upload<T>(nint context, nint buffer, in T value) where T : unmanaged
	{
		MappedSubresource mapped;
		Check(((delegate* unmanaged<nint, nint, uint, uint, uint, MappedSubresource*, int>)Slot(context, 14))(
			context, buffer, 0, 4 /* WRITE_DISCARD */, 0, &mapped), "Map");
		*(T*)mapped.Data = value;
		((delegate* unmanaged<nint, nint, uint, void>)Slot(context, 15))(context, buffer, 0);
	}

	public static nint CreateQuery(nint device, uint type)
	{
		uint* desc = stackalloc uint[2] { type, 0 };
		nint query;
		Check(((delegate* unmanaged<nint, uint*, nint*, int>)Slot(device, 24))(device, desc, &query), "CreateQuery");
		return query;
	}

	public static void Begin(nint context, nint query) => ((delegate* unmanaged<nint, nint, void>)Slot(context, 27))(context, query);

	public static void End(nint context, nint query) => ((delegate* unmanaged<nint, nint, void>)Slot(context, 28))(context, query);

	/// <summary>False until the result is ready, never flushes</summary>
	public static bool GetData(nint context, nint query, void* data, uint size) =>
		((delegate* unmanaged<nint, nint, void*, uint, uint, int>)Slot(context, 29))(context, query, data, size, 1) == 0;

	public static void Dispatch(nint context, uint x, uint y) =>
		((delegate* unmanaged<nint, uint, uint, uint, void>)Slot(context, 41))(context, Math.Max(1, x), Math.Max(1, y), 1);

	public static void ClearUint(nint context, nint uav, uint value)
	{
		uint* values = stackalloc uint[4] { value, value, value, value };
		((delegate* unmanaged<nint, nint, uint*, void>)Slot(context, 51))(context, uav, values);
	}

	public static void ClearFloat(nint context, nint uav, float x, float y = 0, float z = 0, float w = 0)
	{
		float* values = stackalloc float[4] { x, y, z, w };
		((delegate* unmanaged<nint, nint, float*, void>)Slot(context, 52))(context, uav, values);
	}

	public static void SetShaderResources(nint context, nint* views, uint count) =>
		((delegate* unmanaged<nint, uint, uint, nint*, void>)Slot(context, 67))(context, 0, count, views);

	public static void SetUnorderedAccessViews(nint context, nint* views, uint count) =>
		((delegate* unmanaged<nint, uint, uint, nint*, uint*, void>)Slot(context, 68))(context, 0, count, views, null);

	public static void SetShader(nint context, nint shader) =>
		((delegate* unmanaged<nint, nint, nint*, uint, void>)Slot(context, 69))(context, shader, null, 0);

	public static void SetSamplers(nint context, nint* samplers, uint count) =>
		((delegate* unmanaged<nint, uint, uint, nint*, void>)Slot(context, 70))(context, 0, count, samplers);

	public static void SetConstantBuffers(nint context, nint* buffers, uint count) =>
		((delegate* unmanaged<nint, uint, uint, nint*, void>)Slot(context, 71))(context, 0, count, buffers);
}
