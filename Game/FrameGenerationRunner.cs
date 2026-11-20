using System;
using System.Threading;


using FFXIVClientStructs.FFXIV.Client.UI;

using TerraFX.Interop.DirectX;

using UpscaleBuddy.D3D12;
using UpscaleBuddy.FrameGeneration;
using UpscaleBuddy.Ngx;

using Device = FFXIVClientStructs.FFXIV.Client.Graphics.Kernel.Device;
using KernelTexture = FFXIVClientStructs.FFXIV.Client.Graphics.Kernel.Texture;

namespace UpscaleBuddy.Game;

/// <summary>The response of the runner to a generator failure</summary>
internal enum FailureAction
{
	// Sets asyncFailed for async generation, failed for sync generation
	Stop,

	// Calls RuleOutNvidia. The generation mode does not change.
	SwitchToAmd,

	// For a lost device: calls RuleOutNvidia with closeSession, then Stop
	SwitchToAmdAndStop,
}

/// <summary>
/// Render thread. Runs the generator for the current sizes with the upscaler's inputs of the same frame. When a
/// generator fails, the runner drops it. The runner does not try again until the user disables and enables frame
/// generation again.
/// </summary>
public unsafe class FrameGenerationRunner(UpscaleBuddyConfiguration configuration, DlssPath dlssPath): IDisposable
{
	private Bridge? bridge;
	private IFrameGenerator? generator;
	private uint width, height, maxRenderWidth, maxRenderHeight;
	private DlssFrameGenerationModel model;
	private bool failed;
	private int skipped;

	// Only the render thread uses these fields. Each device has one bridge, one calibration and one LUT set.
	private Bridge? asyncBridge;
	private FinishCalibrationResult? calibration;
	private ColorFilterLuts? luts;
	private AsyncGenerator? asyncGenerator;
	private readonly Lock ticketLock = new();
	private GenerationTicket? pendingTicket;

	// The render thread writes these fields. UpdateMode reads them.
	private volatile bool calibrationFailed;
	private volatile bool lutsMissing;
	private volatile bool asyncFailed;

	public string Status { get; private set; } = "Off";

	/// <summary>True while the active generator is NVIDIA DLSS frame generation</summary>
	public bool NvidiaActive => this.generator is NvidiaFrameGenerator || this.asyncGenerator?.IsNvidia == true;

	private GeneratorShape Shape => new(this.width, this.height, this.maxRenderWidth, this.maxRenderHeight, this.model);

	/// <summary>NGX writes its log files here</summary>
	private static string NgxDataPath => Service.PluginInterface.GetPluginConfigDirectory();

	/// <summary>UpdateMode sets this value. It is null when Async is true.</summary>
	public string? SynchronousReason { get; private set; }

	/// <summary>UpdateMode sets this value.</summary>
	public bool Async { get; private set; }

	/// <summary>The value is a running average. It is 0 when no async generator exists.</summary>
	public double AsyncQueueMs => this.asyncGenerator?.RecordedMs ?? 0;

	/// <summary>
	/// Main thread. True when the game sleeps 50 ms after each Present. Framework.Tick does this when
	/// UIModule.ShouldLimitFps is true (AFK with FPSDownAFK, or an inactive window with FPSInActive)
	/// </summary>
	public bool Paused
	{
		get
		{
			UIModule* uiModule = UIModule.Instance();
			return uiModule != null && uiModule->ShouldLimitFps();
		}
	}

	/// <summary>Main thread. True when frame generation is on and not paused</summary>
	public bool Enabled => configuration.FrameGeneration && !this.Paused;

	/// <summary>Main thread. Marks this frame as not generated, so the next Generate resets the history</summary>
	public void Skip() => Interlocked.Exchange(ref this.skipped, 1);

	/// <summary>Call on the main thread, before the UI pass.</summary>
	public void UpdateMode(bool presenterUsable)
	{
		bool filterOn = UiLayer.ToneParameters().Strength > 0;
		string? reason = this.asyncFailed
			? "async generation failed"
			: AsyncBlocker(configuration.FrameGenerationQueue, presenterUsable, !this.calibrationFailed, !this.lutsMissing, filterOn);
		this.SynchronousReason = reason;
		this.Async = reason == null;
	}

	public static bool AsyncAllowed(FrameGenerationQueue queue, bool presenterUsable, bool calibrated, bool lutsReady, bool filterOn) =>
		AsyncBlocker(queue, presenterUsable, calibrated, lutsReady, filterOn) == null;

	/// <summary>Returns null when async generation can run.</summary>
	public static string? AsyncBlocker(FrameGenerationQueue queue, bool presenterUsable, bool calibrated, bool lutsReady, bool filterOn)
	{
		if (queue == FrameGenerationQueue.Synchronous)
			return "set to synchronous";
		if (!presenterUsable)
			return "the presenter thread is unavailable";
		if (!calibrated)
			return "this GPU's rounding differs from the finish pass";
		if (filterOn && !lutsReady)
			return "the colour filter's LUTs did not load";
		return null;
	}

	/// <summary>
	/// Generates the frame between the previous scene and this one into output. Returns false when there is nothing to
	/// generate from
	/// </summary>
	public bool Generate(ID3D11DeviceContext* context, KernelTexture* scene, KernelTexture* output)
	{
		if (!this.TakeInputs(scene, out FrameInputs inputs))
			return false;

		this.ReleaseAsyncGenerator();
		try
		{
			this.Fit(context, scene->ActualWidth, scene->ActualHeight, inputs);
			this.generator!.Generate(context, inputs, (ID3D11Texture2D*)output->D3D11Texture2D);
			this.Status = $"On, {this.generator.Name}";
			return true;
		}
		catch (Exception e)
		{
			switch (ActionAfterFailure(e, this.bridge?.DeviceLost == true, this.generator is NvidiaFrameGenerator))
			{
				case FailureAction.SwitchToAmd:
					this.RuleOutNvidia(e.Message, false);
					Service.PluginLog.Warning(e, "DLSS frame generation failed, AMD frame generation from now on");
					break;
				case FailureAction.SwitchToAmdAndStop:
					this.RuleOutNvidia(e.Message, true);
					this.Fail(e);
					break;
				default:
					this.Fail(e);
					break;
			}

			return false;
		}
	}

	/// <summary>True when e or one of its inner exceptions is an NGX failure</summary>
	internal static bool IsNgxFailure(Exception? e)
	{
		for (; e != null; e = e.InnerException)
		{
			if (e is NgxException)
				return true;
		}

		return false;
	}

	internal static FailureAction ActionAfterFailure(Exception? failure, bool deviceLost, bool nvidiaActive)
	{
		if (!nvidiaActive)
			return FailureAction.Stop;
		if (deviceLost)
			return FailureAction.SwitchToAmdAndStop;
		return IsNgxFailure(failure) ? FailureAction.SwitchToAmd : FailureAction.Stop;
	}

	internal static bool NeedsRecreate(bool exists, GeneratorShape current, GeneratorShape wanted) => !exists || current != wanted;

	/// <summary>Releases the generators and stops the use of NVIDIA frame generation. closeSession also closes the NGX session.</summary>
	private void RuleOutNvidia(string reason, bool closeSession)
	{
		this.Release("Switching to AMD frame generation");
		this.ReleaseAsyncGenerator();
		if (closeSession)
			NgxSession.Shutdown();
		FrameGenerators.RuleOutNvidia(reason);
	}

	private void StopAsync(Exception? failure, bool deviceLost)
	{
		this.asyncFailed = true;
		this.Status = "Async generation failed, synchronous from now on";
		Service.PluginLog.Error(failure, deviceLost ? "The D3D12 device was removed" : "Async frame generation failed");
	}

	/// <summary>Returns null when nothing was queued. TakeTicket returns the ticket.</summary>
	public GenerationTicket? Submit(ID3D11DeviceContext* context, KernelTexture* scene, KernelTexture* ui, bool uiDrawn, in FinishConstants finish,
		int lutIndex)
	{
		if (!this.TakeInputs(scene, out FrameInputs inputs))
			return null;

		this.Release(null);
		try
		{
			if (!this.FitAsync(context, scene->ActualWidth, scene->ActualHeight, inputs))
				return null;
		}
		catch (Exception e)
		{
			this.asyncFailed = true;
			this.ReleaseAsyncGenerator();
			this.Status = "Async generation did not start, synchronous from now on";
			Service.PluginLog.Error(e, "Async frame generation did not start, frame generation stays synchronous");
			return null;
		}

		try
		{
			AsyncGenerator generator = this.asyncGenerator!;
			GenerationTicket? ticket = generator.Submit(context, inputs, (ID3D11Texture2D*)ui->D3D11Texture2D, uiDrawn, finish, lutIndex);
			if (generator.Failed || generator.DeviceLost)
			{
				if (ticket is { } unused)
					unused.Owner.Release(unused);
				bool deviceLost = generator.DeviceLost;
				Exception? failure = generator.Failure;
				switch (ActionAfterFailure(failure, deviceLost, generator.IsNvidia))
				{
					case FailureAction.SwitchToAmd:
						this.RuleOutNvidia(failure!.Message, false);
						Service.PluginLog.Warning(failure, "DLSS frame generation failed, AMD frame generation from now on");
						break;
					case FailureAction.SwitchToAmdAndStop:
						this.RuleOutNvidia("the D3D12 device was removed", true);
						this.StopAsync(failure, deviceLost);
						break;
					default:
						this.StopAsync(failure, deviceLost);
						break;
				}

				return null;
			}

			lock (this.ticketLock)
			{
				if (this.pendingTicket is { } untaken)
					untaken.Owner.Release(untaken);
				this.pendingTicket = ticket;
			}

			this.Status = $"On, {generator.Name}, async on the compute queue";
			return ticket;
		}
		catch (Exception e)
		{
			this.Fail(e);
			return null;
		}
	}

	/// <summary>Call on the main thread, at Present. The caller releases the ticket.</summary>
	public GenerationTicket? TakeTicket()
	{
		lock (this.ticketLock)
		{
			GenerationTicket? ticket = this.pendingTicket;
			this.pendingTicket = null;
			return ticket;
		}
	}

	private bool TakeInputs(KernelTexture* scene, out FrameInputs inputs)
	{
		if (!configuration.FrameGeneration)
		{
			this.failed = false;
			this.asyncFailed = false;
			this.Release("Off");
			this.ReleaseAsync();
			inputs = default;
			return false;
		}

		if (!dlssPath.TakeFrame(out inputs) || inputs.Depth == null || inputs.MotionVectors == null)
		{
			this.Status =
				"Waiting for the upscaler. Set the game's upscaling to AMD FSR, or to NVIDIA DLSS with the DLSS entry selected above.";
			return false;
		}

		if (this.failed)
			return false;

		inputs.Scene = (ID3D11Texture2D*)scene->D3D11Texture2D;
		inputs.Reset |= Interlocked.Exchange(ref this.skipped, 0) != 0;
		return true;
	}

	private void Fail(Exception e)
	{
		this.failed = true;
		this.Release($"Frame generation did not start: {e.Message}");
		this.ReleaseAsyncGenerator();
		Service.PluginLog.Error(e, "Frame generation failed, repeating frames until it's turned off and on again");
	}

	private void Fit(ID3D11DeviceContext* context, uint sceneWidth, uint sceneHeight, in FrameInputs inputs)
	{
		D3D11_TEXTURE2D_DESC depth = Dx.GetDesc(inputs.Depth);
		GeneratorShape wanted = new(sceneWidth, sceneHeight, depth.Width, depth.Height, configuration.DlssFrameGenerationModel);
		if (!NeedsRecreate(this.generator != null, this.Shape, wanted))
			return;

		this.Release("Starting");
		ID3D11Device* device;
		context->GetDevice(&device);
		try
		{
			this.bridge = new Bridge(device);
			this.generator = FrameGenerators.Create(this.bridge, sceneWidth, sceneHeight, depth.Width, depth.Height, inputs.InfiniteFar,
				wanted.Model, NgxDataPath);
			this.model = wanted.Model;
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

	/// <summary>Returns false when async generation can't run.</summary>
	private bool FitAsync(ID3D11DeviceContext* context, uint sceneWidth, uint sceneHeight, in FrameInputs inputs)
	{
		D3D11_TEXTURE2D_DESC depth = Dx.GetDesc(inputs.Depth);
		GeneratorShape wanted = new(sceneWidth, sceneHeight, depth.Width, depth.Height, configuration.DlssFrameGenerationModel);
		if (!NeedsRecreate(this.asyncGenerator != null, this.Shape, wanted))
			return true;

		this.ReleaseAsyncGenerator();
		this.Status = "Starting";
		ID3D11Device* device;
		context->GetDevice(&device);
		try
		{
			this.asyncBridge ??= new Bridge(device, QueueKind.Compute);
			if (this.calibration == null)
			{
				this.calibration = FinishCalibration.Run(device, context, this.asyncBridge.Device12, this.asyncBridge.Queue);
				this.calibrationFailed = !this.calibration.Passed;
				if (this.calibrationFailed)
					Service.PluginLog.Warning($"Async frame generation is off: {this.calibration.Reason}");
			}

			if (this.luts == null)
			{
				this.luts = new ColorFilterLuts(this.asyncBridge.Device12, this.asyncBridge.Queue, path => Service.DataManager.GetFile(path)?.Data);
				this.lutsMissing = !this.luts.Loaded;
				if (this.lutsMissing)
					Service.PluginLog.Warning("The colour filter's LUTs did not load, frame generation stays synchronous with the colour filter on");
			}

			if (!this.calibration.Passed)
				return false;

			this.asyncGenerator = new AsyncGenerator(this.asyncBridge, this.calibration, this.luts, sceneWidth, sceneHeight, depth.Width, depth.Height,
				inputs.InfiniteFar, wanted.Model, NgxDataPath);
			this.model = wanted.Model;
			this.width = sceneWidth;
			this.height = sceneHeight;
			this.maxRenderWidth = depth.Width;
			this.maxRenderHeight = depth.Height;
			return true;
		}
		finally
		{
			device->Release();
		}
	}

	private void Release(string? status)
	{
		this.generator?.Dispose();
		this.generator = null;
		this.bridge?.Dispose();
		this.bridge = null;
		if (status != null)
			this.Status = status;
	}

	/// <summary>Call on the render thread. Blocks until Release for every ticket, for a maximum of 1 s.</summary>
	private void ReleaseAsyncGenerator()
	{
		if (this.asyncGenerator == null)
			return;

		lock (this.ticketLock)
		{
			if (this.pendingTicket is { } untaken)
				untaken.Owner.Release(untaken);
			this.pendingTicket = null;
		}

		this.asyncGenerator.Dispose();
		this.asyncGenerator = null;
	}

	private void ReleaseAsync()
	{
		this.ReleaseAsyncGenerator();
		this.luts?.Dispose();
		this.luts = null;
		this.calibration?.Dispose();
		this.calibration = null;
		this.asyncBridge?.Dispose();
		this.asyncBridge = null;
	}

	/// <summary>After the render thread is done with it (UiLayer.Stop, then the plugin's wait)</summary>
	public void Dispose()
	{
		this.Release("Off");
		this.ReleaseAsync();
		NgxSession.Shutdown();
		GC.SuppressFinalize(this);
	}
}
