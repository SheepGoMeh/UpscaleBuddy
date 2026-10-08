using System;
using System.IO;

using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;

using static TerraFX.Interop.DirectX.D3D_SRV_DIMENSION;
using static TerraFX.Interop.DirectX.D3D11_BIND_FLAG;
using static TerraFX.Interop.DirectX.D3D11_CPU_ACCESS_FLAG;
using static TerraFX.Interop.DirectX.D3D11_FILTER;
using static TerraFX.Interop.DirectX.D3D11_MAP;
using static TerraFX.Interop.DirectX.D3D11_RESOURCE_MISC_FLAG;
using static TerraFX.Interop.DirectX.D3D11_TEXTURE_ADDRESS_MODE;
using static TerraFX.Interop.DirectX.D3D11_UAV_DIMENSION;
using static TerraFX.Interop.DirectX.D3D11_USAGE;
using static TerraFX.Interop.DirectX.DXGI_FORMAT;
using static TerraFX.Interop.Windows.Windows;

namespace UpscaleBuddy;

/// <summary>
/// D3D11 objects the upscalers create in several places, on TerraFX
/// </summary>
public static unsafe class Dx
{
	public const uint BindShaderResourceAndUav = (uint)(D3D11_BIND_SHADER_RESOURCE | D3D11_BIND_UNORDERED_ACCESS);
	public const uint MiscSharedNtHandle = (uint)(D3D11_RESOURCE_MISC_SHARED | D3D11_RESOURCE_MISC_SHARED_NTHANDLE);

	public static T* Query<T>(void* obj) where T : unmanaged, IUnknown.Interface
	{
		T* result;
		ThrowIfFailed(((IUnknown*)obj)->QueryInterface(__uuidof<T>(), (void**)&result));
		return result;
	}

	public static void Release<T>(T* obj) where T : unmanaged, IUnknown.Interface
	{
		if (obj != null)
			obj->Release();
	}

	public static D3D11_TEXTURE2D_DESC GetDesc(ID3D11Texture2D* texture)
	{
		D3D11_TEXTURE2D_DESC desc;
		texture->GetDesc(&desc);
		return desc;
	}

	public static ID3D11Texture2D* CreateTexture(ID3D11Device* device, uint width, uint height, DXGI_FORMAT format, uint mips, uint bind,
		void* data = null, uint pitch = 0, uint misc = 0)
	{
		D3D11_TEXTURE2D_DESC desc = new()
		{
			Width = Math.Max(1, width), Height = Math.Max(1, height), MipLevels = mips, ArraySize = 1, Format = format,
			SampleDesc = new DXGI_SAMPLE_DESC(1, 0), BindFlags = bind, MiscFlags = misc,
		};
		D3D11_SUBRESOURCE_DATA init = new() { pSysMem = data, SysMemPitch = pitch };
		ID3D11Texture2D* texture;
		ThrowIfFailed(device->CreateTexture2D(&desc, data == null ? null : &init, &texture));
		return texture;
	}

	/// <summary>All mips, the texture's own format unless given</summary>
	public static ID3D11ShaderResourceView* CreateSrv(ID3D11Device* device, void* texture, DXGI_FORMAT format = DXGI_FORMAT_UNKNOWN)
	{
		D3D11_SHADER_RESOURCE_VIEW_DESC desc = new() { Format = format, ViewDimension = D3D11_SRV_DIMENSION_TEXTURE2D };
		desc.Texture2D.MipLevels = uint.MaxValue;
		ID3D11ShaderResourceView* view;
		ThrowIfFailed(device->CreateShaderResourceView((ID3D11Resource*)texture, format == DXGI_FORMAT_UNKNOWN ? null : &desc, &view));
		return view;
	}

	public static ID3D11UnorderedAccessView* CreateUav(ID3D11Device* device, void* texture, DXGI_FORMAT format, uint mip = 0)
	{
		D3D11_UNORDERED_ACCESS_VIEW_DESC desc = new() { Format = format, ViewDimension = D3D11_UAV_DIMENSION_TEXTURE2D };
		desc.Texture2D.MipSlice = mip;
		ID3D11UnorderedAccessView* view;
		ThrowIfFailed(device->CreateUnorderedAccessView((ID3D11Resource*)texture, &desc, &view));
		return view;
	}

	public static ID3D11Buffer* CreateConstantBuffer(ID3D11Device* device, uint size)
	{
		D3D11_BUFFER_DESC desc = new()
		{
			ByteWidth = (size + 15) & ~15u, Usage = D3D11_USAGE_DYNAMIC, BindFlags = (uint)D3D11_BIND_CONSTANT_BUFFER,
			CPUAccessFlags = (uint)D3D11_CPU_ACCESS_WRITE,
		};
		ID3D11Buffer* buffer;
		ThrowIfFailed(device->CreateBuffer(&desc, null, &buffer));
		return buffer;
	}

	/// <summary>Clamped, linear or point</summary>
	public static ID3D11SamplerState* CreateSampler(ID3D11Device* device, bool linear)
	{
		D3D11_SAMPLER_DESC desc = new()
		{
			Filter = linear ? D3D11_FILTER_MIN_MAG_MIP_LINEAR : D3D11_FILTER_MIN_MAG_MIP_POINT,
			AddressU = D3D11_TEXTURE_ADDRESS_CLAMP, AddressV = D3D11_TEXTURE_ADDRESS_CLAMP, AddressW = D3D11_TEXTURE_ADDRESS_CLAMP,
			MaxAnisotropy = 1, ComparisonFunc = D3D11_COMPARISON_FUNC.D3D11_COMPARISON_NEVER, MaxLOD = float.MaxValue,
		};
		ID3D11SamplerState* sampler;
		ThrowIfFailed(device->CreateSamplerState(&desc, &sampler));
		return sampler;
	}

	/// <summary>Compute shader from an embedded resource</summary>
	public static ID3D11ComputeShader* CreateComputeShader(ID3D11Device* device, string resource)
	{
		byte[] bytecode = ReadResource(resource);
		ID3D11ComputeShader* shader;
		fixed (byte* code = bytecode)
			ThrowIfFailed(device->CreateComputeShader(code, (nuint)bytecode.Length, null, &shader));
		return shader;
	}

	public static byte[] ReadResource(string name)
	{
		using Stream stream = typeof(Dx).Assembly.GetManifestResourceStream(name) ?? throw new InvalidOperationException($"missing resource {name}");
		using MemoryStream memory = new();
		stream.CopyTo(memory);
		return memory.ToArray();
	}

	public static void Upload<T>(ID3D11DeviceContext* context, ID3D11Buffer* buffer, in T value) where T : unmanaged
	{
		D3D11_MAPPED_SUBRESOURCE mapped;
		ThrowIfFailed(context->Map((ID3D11Resource*)buffer, 0, D3D11_MAP_WRITE_DISCARD, 0, &mapped));
		*(T*)mapped.pData = value;
		context->Unmap((ID3D11Resource*)buffer, 0);
	}

	/// <summary>View format for a typeless depth texture, other formats as is</summary>
	public static DXGI_FORMAT ReadableFormat(DXGI_FORMAT format) => format switch
	{
		DXGI_FORMAT_R24G8_TYPELESS => DXGI_FORMAT_R24_UNORM_X8_TYPELESS,
		DXGI_FORMAT_R32G8X24_TYPELESS => DXGI_FORMAT_R32_FLOAT_X8X24_TYPELESS,
		DXGI_FORMAT_R32_TYPELESS => DXGI_FORMAT_R32_FLOAT,
		_ => format,
	};
}
