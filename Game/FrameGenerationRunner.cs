using System;
using System.Diagnostics;

using TerraFX.Interop.DirectX;

using UpscaleBuddy.Ffx;
using UpscaleBuddy.FrameGeneration;

using KernelTexture = FFXIVClientStructs.FFXIV.Client.Graphics.Kernel.Texture;

namespace UpscaleBuddy.Game;

/// <summary>
/// Render thread: the generator for the current sizes, fed with the upscaler's inputs of the same frame. A failing generator
/// is dropped and not retried until frame generation is turned off and on again
/// </summary>
public unsafe class FrameGenerationRunner(UpscaleBuddyConfiguration configuration, DlssPath dlssPath): IDisposable
{
	private const double LogSeconds = 5;
	private const uint BakedWaitMs = 100;

	private IFrameGenerator? generator;
	private long lastLog;
	private uint width, height, maxRenderWidth, maxRenderHeight;
	private bool failed;

	public string Status { get; private set; } = "Off";

	/// <summary>Where the game's queue spends its time generating, while timings are shown</summary>
	public string Timings => this.generator?.Timings ?? "";

	public bool Enabled => configuration.FrameGeneration;

	/// <summary>
	/// The frame between the previous scene and this one is generated off this thread; null when there's nothing to generate from
	/// or the generator is behind (its next frame starts over)
	/// </summary>
	public GenerationTicket? Submit(ID3D11DeviceContext* context, KernelTexture* scene)
	{
		if (!configuration.FrameGeneration)
		{
			this.failed = false;
			this.Release("Off");
			return null;
		}

		if (!dlssPath.TakeFrame(out FrameInputs inputs) || inputs.Depth == null || inputs.MotionVectors == null)
		{
			this.Status = "Waiting for the upscaler (needs the game's resolution scaling on AMD FSR, or NVIDIA DLSS with the DLSS entry)";
			return null;
		}

		if (this.failed)
			return null;

		inputs.Scene = (ID3D11Texture2D*)scene->D3D11Texture2D;
		inputs.Measure = configuration.ShowTimings;
		try
		{
			this.Fit(context, scene->ActualWidth, scene->ActualHeight, inputs);
			GenerationTicket? ticket = this.generator!.Submit(context, inputs);
			this.Status = $"On, {this.generator.Name}";
			if (inputs.Measure && Stopwatch.GetElapsedTime(this.lastLog).TotalSeconds >= LogSeconds)
			{
				this.lastLog = Stopwatch.GetTimestamp();
				Service.PluginLog.Information("Frame generation timing: " + this.generator.Timings);
			}

			return ticket;
		}
		catch (Exception e)
		{
			this.failed = true;
			this.Release($"Failed: {e.Message}");
			Service.PluginLog.Error(e, "Frame generation failed, repeating frames until it's turned off and on again");
			return null;
		}
	}

	/// <summary>Baked frames: <see cref="Submit"/>, then this thread waits for the frame and copies it into output; false when there's none</summary>
	public bool Generate(ID3D11DeviceContext* context, KernelTexture* scene, KernelTexture* output)
	{
		GenerationTicket? ticket = this.Submit(context, scene);
		if (ticket == null)
			return false;

		if (!ticket.Wait(BakedWaitMs) || !ticket.TryAcquire(context))
			return false;

		context->CopyResource((ID3D11Resource*)output->D3D11Texture2D, (ID3D11Resource*)ticket.Output);
		ticket.Release();
		return true;
	}

	private void Fit(ID3D11DeviceContext* context, uint sceneWidth, uint sceneHeight, in FrameInputs inputs)
	{
		D3D11_TEXTURE2D_DESC depth = Dx.GetDesc(inputs.Depth);
		if (this.generator != null && sceneWidth == this.width && sceneHeight == this.height &&
		    depth.Width == this.maxRenderWidth && depth.Height == this.maxRenderHeight)
			return;

		this.Release("Starting");
		ID3D11Device* device;
		context->GetDevice(&device);
		try
		{
			this.generator = new FfxFrameGenerator(device, sceneWidth, sceneHeight, depth.Width, depth.Height, inputs.InfiniteFar);
			this.width = sceneWidth;
			this.height = sceneHeight;
			this.maxRenderWidth = depth.Width;
			this.maxRenderHeight = depth.Height;
		}
		finally
		{
			device->Release();
		}
	}

	private void Release(string status)
	{
		this.generator?.Dispose();
		this.generator = null;
		this.Status = status;
	}

	/// <summary>After the render thread is done with it (UiLayer.Stop, then the plugin's wait)</summary>
	public void Dispose()
	{
		this.Release("Off");
		GC.SuppressFinalize(this);
	}
}
