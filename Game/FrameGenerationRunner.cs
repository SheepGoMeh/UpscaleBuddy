using System;

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
	private IFrameGenerator? generator;
	private uint width, height, maxRenderWidth, maxRenderHeight;
	private bool failed;

	public string Status { get; private set; } = "Off";

	public bool Enabled => configuration.FrameGeneration;

	/// <summary>The frame between the previous scene and this one into output; false when there's nothing to generate from</summary>
	public bool Generate(ID3D11DeviceContext* context, KernelTexture* scene, KernelTexture* output)
	{
		if (!configuration.FrameGeneration)
		{
			this.failed = false;
			this.Release("Off");
			return false;
		}

		if (!dlssPath.TakeFrame(out FrameInputs inputs) || inputs.Depth == null || inputs.MotionVectors == null)
		{
			this.Status = "Waiting for the upscaler (needs the game's resolution scaling on AMD FSR)";
			return false;
		}

		if (this.failed)
			return false;

		inputs.Scene = (ID3D11Texture2D*)scene->D3D11Texture2D;
		try
		{
			this.Fit(context, scene->ActualWidth, scene->ActualHeight, inputs);
			this.generator!.Generate(context, inputs, (ID3D11Texture2D*)output->D3D11Texture2D);
			this.Status = $"On, {this.generator.Name}";
			return true;
		}
		catch (Exception e)
		{
			this.failed = true;
			this.Release($"Failed: {e.Message}");
			Service.PluginLog.Error(e, "Frame generation failed, repeating frames until it's turned off and on again");
			return false;
		}
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
