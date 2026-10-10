using System;

using TerraFX.Interop.DirectX;

using UpscaleBuddy.Fsr3;

using static TerraFX.Interop.DirectX.DXGI_FORMAT;

namespace UpscaleBuddy.D3D12;

/// <summary>
/// Runs a D3D12 upscaler library over the bridge: game textures shared with D3D12 are read directly, others copied into twins
/// </summary>
public unsafe class D3D12Upscaler: IUpscaler
{
	private readonly Bridge bridge;
	private readonly IBackend? backend;

	// D3D11 marks: start, inputs copied, D3D12 done, output copied
	private readonly GpuTimer timer;

	// Created on the first dispatch, in the game's formats
	private Twin? color, depth, motionVectors, output;
	private bool directColorUsed, directMotionVectorsUsed, directOutputUsed;

	public D3D12Upscaler(ID3D11Device* device, uint maxRenderWidth, uint maxRenderHeight, uint upscaleWidth, uint upscaleHeight,
		BackendFactory createBackend)
	{
		this.MaxRenderWidth = maxRenderWidth;
		this.MaxRenderHeight = maxRenderHeight;
		this.UpscaleWidth = upscaleWidth;
		this.UpscaleHeight = upscaleHeight;

		this.bridge = new Bridge(device);
		try
		{
			this.timer = new GpuTimer(device, 4);
			this.backend = createBackend(this.bridge.Device12);
		}
		catch
		{
			this.Dispose();
			throw;
		}
	}

	public string Name => this.backend!.Name;

	public uint MaxRenderWidth { get; }

	public uint MaxRenderHeight { get; }

	public uint UpscaleWidth { get; }

	public uint UpscaleHeight { get; }

	/// <summary>Handoff = D3D11 waiting on D3D12 minus the backend: fence latency, queue switches, late D3D12 submission</summary>
	public string Timings
	{
		get
		{
			double[] ms = this.timer.Ms;
			double backendMs = this.bridge.RecordedMs;
			return $"GPU {ms[0] + ms[1] + ms[2]:F3} ms = copy in {ms[0]:F3} + {this.backend!.Label} {backendMs:F3} + " +
			       $"handoff {ms[1] - backendMs:F3} + copy out {ms[2]:F3}, CPU stall on allocators {this.bridge.CpuStallMs:F3} ms, " +
			       $"zero copy: color {this.directColorUsed}, motion vectors {this.directMotionVectorsUsed}, output {this.directOutputUsed}";
		}
	}

	public void Dispatch(ID3D11DeviceContext* context, in Fsr3Upscaler.DispatchParams p)
	{
		if (p.Measure)
			this.timer.Begin(context);

		Twin? directColor = this.bridge.Direct(p.Color);
		Twin? directMotionVectors = this.bridge.Direct(p.MotionVectors);
		Twin? directOutput = this.bridge.Direct(p.Output);
		this.directColorUsed = directColor != null;
		this.directMotionVectorsUsed = directMotionVectors != null;
		this.directOutputUsed = directOutput != null;

		Twin colorTwin = directColor ?? (this.color ??= this.bridge.CreateTwin(Dx.GetDesc(p.Color).Format, this.MaxRenderWidth, this.MaxRenderHeight));
		Twin motionVectorsTwin = directMotionVectors ??
		                         (this.motionVectors ??= this.bridge.CreateTwin(Dx.GetDesc(p.MotionVectors).Format, this.MaxRenderWidth, this.MaxRenderHeight));
		Twin outputTwin = directOutput ?? (this.output ??= this.bridge.CreateTwin(Dx.GetDesc(p.Output).Format, this.UpscaleWidth, this.UpscaleHeight));
		Twin depthTwin = this.depth ??= this.bridge.CreateTwin(DXGI_FORMAT_R32_FLOAT, this.MaxRenderWidth, this.MaxRenderHeight);

		// D3D11: game textures into the twins unless D3D12 reads them directly
		if (directColor == null)
			Bridge.Copy(context, colorTwin.Texture11, p.Color, colorTwin.Input);
		if (directMotionVectors == null)
			Bridge.Copy(context, motionVectorsTwin.Texture11, p.MotionVectors, motionVectorsTwin.Input);
		this.bridge.CopyDepth(context, p.Depth, depthTwin, this.MaxRenderWidth, this.MaxRenderHeight);
		if (p.Measure)
			this.timer.Mark(context, 1);

		Fsr3Upscaler.DispatchParams parameters = p;
		IBackend backend = this.backend!;
		uint upscaleWidth = this.UpscaleWidth;
		uint upscaleHeight = this.UpscaleHeight;
		this.bridge.Execute(context,
			list => backend.Record(list, colorTwin.Input, motionVectorsTwin.Input, depthTwin.Input, outputTwin.Input, parameters, upscaleWidth, upscaleHeight),
			p.Measure);

		// The output twin back into the game's output
		if (p.Measure)
			this.timer.Mark(context, 2);
		if (directOutput == null)
			Bridge.Copy(context, p.Output, outputTwin.Texture11, outputTwin.Input);
		if (p.Measure)
		{
			this.timer.Mark(context, 3);
			this.timer.End(context);
		}
	}

	public void Dispose()
	{
		// The D3D12 queue can still be running the last frames, the backend's context too
		this.bridge.WaitIdle();
		this.backend?.Dispose();
		Bridge.Release(this.color);
		Bridge.Release(this.depth);
		Bridge.Release(this.motionVectors);
		Bridge.Release(this.output);
		this.color = this.depth = this.motionVectors = this.output = null;
		this.timer?.Dispose();
		this.bridge.Dispose();
		GC.SuppressFinalize(this);
	}
}
