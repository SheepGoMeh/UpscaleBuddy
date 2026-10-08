using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;

using static TerraFX.Interop.DirectX.DXGI_FORMAT;

namespace UpscaleBuddy.Fsr3;

/// <summary>
/// Supersampled color to the output size, with an intermediate texture for FSR at the supersampled size
/// </summary>
public unsafe class Downsampler: IDisposable
{
	[StructLayout(LayoutKind.Sequential)]
	private struct Constants
	{
		public float SourceScaleX, SourceScaleY;
		public float OutputSizeInvX, OutputSizeInvY;
		public uint OutputWidth, OutputHeight;
		public uint Padding0, Padding1;
	}

	private readonly ID3D11Device* device;
	private readonly ID3D11ComputeShader* shader;
	private readonly ID3D11Buffer* constantBuffer;
	private readonly ID3D11SamplerState* sampler;

	// Views by texture, they keep the textures alive so an address can't be reused
	private readonly Dictionary<nint, nint> srvs = [];
	private readonly Dictionary<nint, nint> uavs = [];

	public Downsampler(ID3D11Device* device, uint width, uint height)
	{
		this.device = device;
		this.Intermediate = Dx.CreateTexture(device, width, height, DXGI_FORMAT_R16G16B16A16_FLOAT, 1, Dx.BindShaderResourceAndUav);
		this.shader = Dx.CreateComputeShader(device, "Shaders.downsample.cso");
		this.constantBuffer = Dx.CreateConstantBuffer(device, (uint)sizeof(Constants));
		this.sampler = Dx.CreateSampler(device, true);
	}

	/// <summary>FSR output at the supersampled size</summary>
	public ID3D11Texture2D* Intermediate { get; }

	/// <summary>Render thread, output size is the used region, the texture can be allocated larger</summary>
	public void Dispatch(ID3D11DeviceContext* context, ID3D11Texture2D* color, uint renderWidth, uint renderHeight, ID3D11Texture2D* output,
		uint outputWidth, uint outputHeight)
	{
		D3D11_TEXTURE2D_DESC colorDesc = Dx.GetDesc(color);
		D3D11_TEXTURE2D_DESC outputDesc = Dx.GetDesc(output);
		Constants constants = new()
		{
			SourceScaleX = renderWidth / (float)colorDesc.Width,
			SourceScaleY = renderHeight / (float)colorDesc.Height,
			OutputSizeInvX = 1.0f / outputWidth,
			OutputSizeInvY = 1.0f / outputHeight,
			OutputWidth = outputWidth,
			OutputHeight = outputHeight,
		};
		Dx.Upload(context, this.constantBuffer, constants);

		if (!this.srvs.TryGetValue((nint)color, out nint srvView))
			this.srvs[(nint)color] = srvView = (nint)Dx.CreateSrv(this.device, color, colorDesc.Format);
		if (!this.uavs.TryGetValue((nint)output, out nint uavView))
			this.uavs[(nint)output] = uavView = (nint)Dx.CreateUav(this.device, output, outputDesc.Format);

		ID3D11ShaderResourceView* srv = (ID3D11ShaderResourceView*)srvView;
		ID3D11UnorderedAccessView* uav = (ID3D11UnorderedAccessView*)uavView;
		ID3D11Buffer* buffer = this.constantBuffer;
		ID3D11SamplerState** samplers = stackalloc ID3D11SamplerState*[2] { null, this.sampler };
		ID3D11ShaderResourceView* noSrv = null;
		ID3D11UnorderedAccessView* noUav = null;

		context->CSSetShaderResources(0, 1, &srv);
		context->CSSetUnorderedAccessViews(0, 1, &uav, null);
		context->CSSetConstantBuffers(0, 1, &buffer);
		context->CSSetSamplers(0, 2, samplers);
		context->CSSetShader(this.shader, null, 0);
		context->Dispatch((outputWidth + 7) / 8, (outputHeight + 7) / 8, 1);

		context->CSSetUnorderedAccessViews(0, 1, &noUav, null);
		context->CSSetShaderResources(0, 1, &noSrv);
		context->CSSetShader(null, null, 0);
	}

	public void Dispose()
	{
		foreach (nint view in this.srvs.Values)
			Dx.Release((IUnknown*)view);
		foreach (nint view in this.uavs.Values)
			Dx.Release((IUnknown*)view);
		this.srvs.Clear();
		this.uavs.Clear();
		Dx.Release(this.shader);
		Dx.Release(this.constantBuffer);
		Dx.Release(this.sampler);
		Dx.Release(this.Intermediate);
		GC.SuppressFinalize(this);
	}
}
