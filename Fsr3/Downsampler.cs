using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

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

	private readonly nint device;
	private readonly nint shader;
	private readonly nint constantBuffer;
	private readonly nint sampler;
	private readonly Dictionary<(nint, bool), nint> views = [];

	public Downsampler(nint device, uint width, uint height)
	{
		this.device = device;
		this.Intermediate = D3D11.CreateTexture(device, width, height, D3D11.FormatR16G16B16A16Float, 1,
			D3D11.BindShaderResource | D3D11.BindUnorderedAccess);
		using Stream stream = typeof(Downsampler).Assembly.GetManifestResourceStream("Shaders.downsample.cso") ??
		                      throw new InvalidOperationException("missing resource Shaders.downsample.cso");
		using MemoryStream memory = new();
		stream.CopyTo(memory);
		this.shader = D3D11.CreateComputeShader(device, memory.ToArray());
		this.constantBuffer = D3D11.CreateConstantBuffer(device, (uint)sizeof(Constants));
		this.sampler = D3D11.CreateSampler(device, true);
	}

	/// <summary>FSR output at the supersampled size</summary>
	public nint Intermediate { get; }

	/// <summary>Render thread, output size is the used region, the texture can be allocated larger</summary>
	public void Dispatch(nint context, nint color, uint renderWidth, uint renderHeight, nint output, uint outputWidth, uint outputHeight)
	{
		D3D11.Texture2DDesc colorDesc = D3D11.GetDesc(color);
		D3D11.Texture2DDesc outputDesc = D3D11.GetDesc(output);
		Constants constants = new()
		{
			SourceScaleX = renderWidth / (float)colorDesc.Width,
			SourceScaleY = renderHeight / (float)colorDesc.Height,
			OutputSizeInvX = 1.0f / outputWidth,
			OutputSizeInvY = 1.0f / outputHeight,
			OutputWidth = outputWidth,
			OutputHeight = outputHeight,
		};
		D3D11.Upload(context, this.constantBuffer, constants);

		nint* srv = stackalloc nint[1] { this.View(color, false, colorDesc.Format) };
		nint* uav = stackalloc nint[1] { this.View(output, true, outputDesc.Format) };
		nint* buffer = stackalloc nint[1] { this.constantBuffer };
		nint* samplers = stackalloc nint[2] { 0, this.sampler };
		nint* none = stackalloc nint[1] { 0 };

		D3D11.SetShaderResources(context, srv, 1);
		D3D11.SetUnorderedAccessViews(context, uav, 1);
		D3D11.SetConstantBuffers(context, buffer, 1);
		D3D11.SetSamplers(context, samplers, 2);
		D3D11.SetShader(context, this.shader);
		D3D11.Dispatch(context, (outputWidth + 7) / 8, (outputHeight + 7) / 8);

		D3D11.SetUnorderedAccessViews(context, none, 1);
		D3D11.SetShaderResources(context, none, 1);
		D3D11.SetShader(context, 0);
	}

	private nint View(nint texture, bool unordered, uint format)
	{
		if (this.views.TryGetValue((texture, unordered), out nint view))
			return view;

		view = unordered ? D3D11.CreateUav(this.device, texture, format) : D3D11.CreateSrv(this.device, texture, format);
		this.views[(texture, unordered)] = view;
		return view;
	}

	public void Dispose()
	{
		foreach (nint view in this.views.Values)
			D3D11.Release(view);
		this.views.Clear();
		D3D11.Release(this.shader);
		D3D11.Release(this.constantBuffer);
		D3D11.Release(this.sampler);
		D3D11.Release(this.Intermediate);
		GC.SuppressFinalize(this);
	}
}
