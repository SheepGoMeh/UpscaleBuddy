using System;
using System.Linq;

using TerraFX.Interop.DirectX;

using UpscaleBuddy.D3D12;

using static TerraFX.Interop.DirectX.D3D11_BIND_FLAG;
using static TerraFX.Interop.DirectX.D3D11_CPU_ACCESS_FLAG;
using static TerraFX.Interop.DirectX.D3D11_MAP;
using static TerraFX.Interop.DirectX.D3D11_USAGE;
using static TerraFX.Interop.DirectX.DXGI_FORMAT;
using static TerraFX.Interop.Windows.Windows;

namespace UpscaleBuddy.Game;

/// <summary>BlendLut is a 256^3 R8_UINT texture for the blend texture of Shaders/finish.hlsl.</summary>
public sealed unsafe class FinishCalibrationResult: IDisposable
{
	public bool Passed;
	public string Reason = "";
	public ID3D12Resource* BlendLut;

	public void Dispose()
	{
		Dx.Release(this.BlendLut);
		this.BlendLut = null;
	}
}

/// <summary>
/// Measures LayerBlend into BlendLut. Compares the render target conversion of this device with Unorm8 in
/// Shaders/finish.hlsl. Call one time for each device, on the render thread, with the game's immediate context.
/// </summary>
public static unsafe class FinishCalibration
{
	private const uint Size = 4096;
	private const uint Thresholds = 255;

	public static FinishCalibrationResult Run(ID3D11Device* device, ID3D11DeviceContext* context, ID3D12Device* device12, ID3D12CommandQueue* queue)
	{
		FinishCalibrationResult result = new();
		using LayerBlend blend = new();
		byte[]? lut = MeasureBlend(device, context, blend);
		if (lut == null)
		{
			result.Reason = "the UI blend differs per channel";
			return result;
		}

		foreach (DXGI_FORMAT format in new[] { DXGI_FORMAT_R8G8B8A8_UNORM, DXGI_FORMAT_B8G8R8A8_UNORM })
		{
			if (!MatchesModel(MeasureThresholds(device, context, blend, format)))
			{
				result.Reason = $"the render target conversion ({format}) differs from the finish pass model";
				return result;
			}
		}

		result.BlendLut = Upload.Texture3D(device12, queue, DXGI_FORMAT_R8_UINT, 256, lut, 1);
		result.Passed = true;
		return result;
	}

	/// <summary>
	/// For each k in 0..254, returns the smallest float that Unorm8 in finish.hlsl converts to k + 1, as float bits.
	/// Unorm8 truncates to fp16, so each threshold is an fp16 value.
	/// </summary>
	public static uint[] ModelThresholds()
	{
		uint[] thresholds = new uint[Thresholds];
		uint k = 0;
		for (ushort bits = 0; bits <= 0x3C00 && k < Thresholds; bits++)
		{
			float value = (float)BitConverter.UInt16BitsToHalf(bits);
			uint converted = (uint)MathF.Floor((value * 255.0f) + 0.5f);
			while (k < Thresholds && converted >= k + 1)
				thresholds[k++] = BitConverter.SingleToUInt32Bits(value);
		}

		return thresholds;
	}

	public static bool MatchesModel(uint[] thresholds) => thresholds.SequenceEqual(ModelThresholds());

	/// <summary>
	/// Returns the model threshold for each k where the target converts the threshold to k + 1 and the float below it
	/// to k. Returns 0 for the other k. The conversion is monotonic, so a match means that the device threshold is the
	/// model threshold.
	/// </summary>
	private static uint[] MeasureThresholds(ID3D11Device* device, ID3D11DeviceContext* context, LayerBlend blend, DXGI_FORMAT format)
	{
		uint[] model = ModelThresholds();
		float[] values = new float[Thresholds * 2];
		for (int k = 0; k < Thresholds; k++)
		{
			values[k * 2] = BitConverter.UInt32BitsToSingle(model[k]);
			values[(k * 2) + 1] = BitConverter.UInt32BitsToSingle(model[k] - 1);
		}

		ID3D11Texture2D* source;
		fixed (float* data = values)
			source = Dx.CreateTexture(device, Thresholds * 2, 1, DXGI_FORMAT_R32_FLOAT, 1, (uint)D3D11_BIND_SHADER_RESOURCE, data, Thresholds * 2 * 4);
		byte[] bytes = DrawAndRead(device, context, blend, format, Thresholds * 2, 1, source, null, false);
		source->Release();

		int red = format == DXGI_FORMAT_B8G8R8A8_UNORM ? 2 : 0;
		uint[] measured = new uint[Thresholds];
		for (int k = 0; k < Thresholds; k++)
		{
			bool upper = bytes[(k * 2 * 4) + red] == k + 1;
			bool lower = bytes[(((k * 2) + 1) * 4) + red] == k;
			measured[k] = upper && lower ? model[k] : 0;
		}

		return measured;
	}

	/// <summary>
	/// Draws every (destination d, source s, source alpha a) through LayerBlend into a B8G8R8A8 target. Texel x is
	/// d + 256 * (s % 16), and texel y is a + 256 * (s / 16). The destination has d in every channel. The source has s
	/// in R, G and B. Returns the LUT (x = d, y = s, z = a). Returns null when R, G and B differ, or when alpha differs
	/// from the colour result at s = a.
	/// </summary>
	private static byte[]? MeasureBlend(ID3D11Device* device, ID3D11DeviceContext* context, LayerBlend blend)
	{
		uint[] destination = new uint[Size * Size], source = new uint[Size * Size];
		for (uint y = 0; y < Size; y++)
		for (uint x = 0; x < Size; x++)
		{
			uint d = x & 255, s = (x >> 8) + (16 * (y >> 8)), a = y & 255;
			destination[(y * Size) + x] = d * 0x01010101u;
			source[(y * Size) + x] = (a << 24) | (s * 0x010101u);
		}

		ID3D11Texture2D* sourceTexture;
		fixed (uint* data = source)
			sourceTexture = Dx.CreateTexture(device, Size, Size, DXGI_FORMAT_B8G8R8A8_UNORM, 1, (uint)D3D11_BIND_SHADER_RESOURCE, data, Size * 4);
		byte[] bytes = DrawAndRead(device, context, blend, DXGI_FORMAT_B8G8R8A8_UNORM, Size, Size, sourceTexture, destination, true);
		sourceTexture->Release();

		byte[] lut = new byte[256 * 256 * 256];
		for (uint i = 0; i < Size * Size; i++)
		{
			uint x = i % Size, y = i / Size;
			uint d = x & 255, s = (x >> 8) + (16 * (y >> 8)), a = y & 255;
			byte b = bytes[i * 4], g = bytes[(i * 4) + 1], r = bytes[(i * 4) + 2];
			if (r != g || r != b)
				return null;
			lut[(((a * 256) + s) * 256) + d] = r;
		}

		// Alpha uses the same blend factors, so at s = a the alpha result must equal the colour result.
		for (uint i = 0; i < Size * Size; i++)
		{
			uint x = i % Size, y = i / Size;
			uint d = x & 255, a = y & 255;
			if (bytes[(i * 4) + 3] != lut[(((a * 256) + a) * 256) + d])
				return null;
		}

		return lut;
	}

	/// <summary>initial holds the texels of the target. When initial is null, the target has no initial data.</summary>
	private static byte[] DrawAndRead(ID3D11Device* device, ID3D11DeviceContext* context, LayerBlend blend, DXGI_FORMAT format, uint width,
		uint height, ID3D11Texture2D* source, uint[]? initial, bool blended)
	{
		uint bind = (uint)(D3D11_BIND_RENDER_TARGET | D3D11_BIND_SHADER_RESOURCE);
		ID3D11Texture2D* target;
		fixed (uint* data = initial)
			target = Dx.CreateTexture(device, width, height, format, 1, bind, data, width * 4);
		ID3D11RenderTargetView* targetView;
		ThrowIfFailed(device->CreateRenderTargetView((ID3D11Resource*)target, null, &targetView));
		ID3D11ShaderResourceView* sourceView = Dx.CreateSrv(device, source);
		ID3D11Texture2D* staging = null;
		try
		{
			blend.Draw(context, targetView, width, height, sourceView, blended);

			D3D11_TEXTURE2D_DESC desc = Dx.GetDesc(target);
			desc.Usage = D3D11_USAGE_STAGING;
			desc.BindFlags = 0;
			desc.CPUAccessFlags = (uint)D3D11_CPU_ACCESS_READ;
			ThrowIfFailed(device->CreateTexture2D(&desc, null, &staging));
			context->CopyResource((ID3D11Resource*)staging, (ID3D11Resource*)target);
			D3D11_MAPPED_SUBRESOURCE mapped;
			ThrowIfFailed(context->Map((ID3D11Resource*)staging, 0, D3D11_MAP_READ, 0, &mapped));
			byte[] bytes = new byte[width * height * 4];
			for (uint y = 0; y < height; y++)
				new ReadOnlySpan<byte>((byte*)mapped.pData + (y * mapped.RowPitch), (int)width * 4).CopyTo(bytes.AsSpan((int)(y * width * 4)));
			context->Unmap((ID3D11Resource*)staging, 0);
			return bytes;
		}
		finally
		{
			Dx.Release(staging);
			sourceView->Release();
			targetView->Release();
			target->Release();
		}
	}
}
