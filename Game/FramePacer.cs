using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;

using Dalamud.Hooking;

using FFXIVClientStructs.FFXIV.Client.Graphics.Kernel;

using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;

using static TerraFX.Interop.Windows.Windows;

using Framework = FFXIVClientStructs.FFXIV.Client.System.Framework.Framework;

namespace UpscaleBuddy.Game;

/// <summary>
/// Replaces the game's frame limiter. The game sleeps inside SwapChain.Present after it builds the frame. Thus the
/// frame that reaches the screen is a whole sleep old. This class sleeps after Framework.Tick, before the next message
/// pump and input read. The sleep length makes the frame complete just in time for an absolute Present schedule.
/// Frame generation: the base rate is refresh/2. The class presents the generated frame first and the real frame half
/// a frame later.
/// </summary>
public unsafe class FramePacer: IDisposable
{
	// Start the frame this much before the slowest recent one would need
	private static readonly long Margin = Stopwatch.Frequency / 2000;

	// A Present this long before a vblank is on screen at it
	private static readonly long VBlankLead = Stopwatch.Frequency * 7 / 2000;

	private delegate void PresentDelegate(SwapChain* swapChain);

	private readonly UpscaleBuddyConfiguration configuration;
	private readonly UiLayer uiLayer;
	private readonly FrameGenerationRunner frameGeneration;
	private readonly ReShadeEffects reShade;
	private readonly Hook<Framework.Delegates.Tick>? tickHook;
	private readonly Hook<PresentDelegate>? presentHook;
	private readonly Presenter presenter;
	private readonly HighResolutionTimer timer = new();

	private readonly Action presentWithoutLimit;
	private SwapChain* presenting;
	private uint presentingSyncInterval;

	private volatile bool stopping;

	// Main thread. The time from tick start to Present for each of the last frames
	private readonly long[] work = new long[32];
	private int workIndex;

	// Frame generation: the longer half of each of the last frames. A half is the first Present, or this frame's work +
	// the second Present.
	private readonly long[] halves = new long[32];
	private int halfIndex;
	private long tickStart;
	private long deadline;
	private bool presented;

	// Back buffer content for the second Present. The discard swap effect leaves the back buffer undefined.
	private ID3D11Texture2D* held;

	// DWM's vblank grid. The pacer reads it when it aligns the schedule.
	private long vblank;
	private long vblankPeriod;

	// Frame generation headroom: which of the last frames' first Present missed its vblank
	private readonly bool[] late = new bool[120];
	private int lateIndex;
	private int lateCount;

	public FramePacer(UpscaleBuddyConfiguration configuration, UiLayer uiLayer, FrameGenerationRunner frameGeneration,
		ReShadeEffects reShade)
	{
		this.configuration = configuration;
		this.uiLayer = uiLayer;
		this.frameGeneration = frameGeneration;
		this.reShade = reShade;
		this.presenter = new Presenter(uiLayer, reShade);
		this.presentWithoutLimit = this.PresentWithoutLimit;
		try
		{
			this.tickHook = Service.GameInteropProvider.HookFromAddress<Framework.Delegates.Tick>(
				(nint)Framework.StaticVirtualTablePointer->Tick, this.TickDetour);
			this.presentHook = Service.GameInteropProvider.HookFromAddress<PresentDelegate>(
				(nint)SwapChain.MemberFunctionPointers.Present, this.PresentDetour);
			this.tickHook.Enable();
			this.presentHook.Enable();
			this.Available = true;
		}
		catch (Exception e)
		{
			Service.PluginLog.Warning(e, "FramePacer unavailable");
		}
	}

	public bool Available { get; }

	/// <summary>
	/// True when frame generation keeps up with the target. It changes to false when over 5% of the last 120 frames
	/// lost their generated frame. A frame loses it when its first Present is later than its vblank lead. Then the
	/// first Present flips together with the second. It changes to true again under 1%. The two limits stop flicker.
	/// </summary>
	public bool KeepingUp { get; private set; } = true;

	/// <summary>True when Dalamud draws inside Present1. Frame generation then presents on the game's thread</summary>
	public bool DalamudInPresent1 => this.presenter.DalamudInPresent1;

	/// <summary>
	/// The target that the recent frames would hold. Each rendered frame has two halves:
	/// - The first Present.
	/// - The next frame's work and the second Present.
	/// The shown rate is at most 1 / the longer half, from the slowest of the last frames. The value includes the time
	/// that other software spends inside Present (overlays, capture tools).
	/// </summary>
	public int SustainedTarget
	{
		get
		{
			long slowest = this.halves.Max();
			return slowest > 0 ? (int)(Stopwatch.Frequency / slowest) & ~1 : 0;
		}
	}

	/// <summary>The display's refresh rate, 0 when unknown</summary>
	public int RefreshRate
	{
		get
		{
			Device* device = Device.Instance();
			return device != null ? device->FrameRate : 0;
		}
	}

	/// <summary>
	/// Frame generation's rendered rate. The display shows frames at twice this rate. The value is 0 when frame
	/// generation is off or paused, or when the refresh rate is unknown.
	/// </summary>
	public int GenerationRate
	{
		get
		{
			Device* device = Device.Instance();
			return this.frameGeneration.Enabled && device != null ? this.GeneratedRate(device) : 0;
		}
	}

	private bool TickDetour(Framework* framework)
	{
		bool result = this.tickHook!.Original(framework);
		if (this.presented)
		{
			this.presented = false;
			this.timer.Wait(this.deadline - this.PredictedWork());
		}

		this.tickStart = Stopwatch.GetTimestamp();
		return result;
	}

	private void PresentDetour(SwapChain* swapChain)
	{
		Device* device = Device.Instance();
		int generated = this.frameGeneration.Enabled ? this.GeneratedRate(device) : 0;
		bool generate = generated != 0;
		bool game = swapChain == device->SwapChain;
		if (!generate)
			this.presenter.ResetFailure();
		bool threaded = generate && game && !this.stopping && this.presenter.Usable(swapChain);
		if (!threaded)
			this.presenter.Release();
		if (!generate && game)
			this.uiLayer.DiscardRegenerated();
		bool effects = game && this.reShade.TakeRendered();

		int rate = generate ? generated : this.LimitedRate(device);
		if (!game || rate == 0)
		{
			this.deadline = 0;
			this.ResetHeadroom();
			this.presentHook!.Original(swapChain);
			return;
		}

		long now = Stopwatch.GetTimestamp();
		long period = Stopwatch.Frequency / rate;
		long frameWork = this.tickStart != 0 ? now - this.tickStart : 0;
		if (this.tickStart != 0)
		{
			this.work[this.workIndex] = frameWork;
			this.workIndex = (this.workIndex + 1) % this.work.Length;
		}

		if (generate)
			this.TrackHeadroom(this.deadline != 0 && now > this.deadline + VBlankLead);
		else
			this.ResetHeadroom();

		// Align again on the first frame, or when the pacer is behind by more than the second Present's slot. In
		// that case the second Present would go out right after the first in the same vblank. Then the generated
		// frame would not show.
		long behind = generate ? period / 2 : period;
		if (this.deadline == 0 || now - this.deadline > behind)
			this.deadline = this.AlignToVBlank(now);

		this.timer.Wait(this.deadline);

		this.presenting = swapChain;
		uint syncInterval = this.configuration.FrameGenerationVsync ? 1u : 0u;
		this.presentingSyncInterval = syncInterval;
		long presentStart = Stopwatch.GetTimestamp();
		if (threaded && this.presenter.Present(device, swapChain, this.deadline + period / 2, syncInterval, effects, this.presentWithoutLimit))
		{
			this.halves[this.halfIndex] = (frameWork + Stopwatch.GetTimestamp() - presentStart) / 2;
			this.halfIndex = (this.halfIndex + 1) % this.halves.Length;
			this.deadline += period;
			this.presented = true;
			return;
		}

		// Cap 0 skips the game's sleep. Preset 1 is sync interval 1.
		uint preset = device->FrameRateLimitPresetPresent;
		ushort cap = device->FrameRateLimitPresent;
		device->FrameRateLimitPresent = 0;
		if (generate)
		{
			device->FrameRateLimitPresetPresent = syncInterval;
			(long first, long second) = this.PresentTwice(device, swapChain, period, syncInterval, effects);
			this.halves[this.halfIndex] = Math.Max(first, frameWork + second);
			this.halfIndex = (this.halfIndex + 1) % this.halves.Length;
		}
		else
		{
			this.presentHook!.Original(swapChain);
		}

		device->FrameRateLimitPresetPresent = preset;
		device->FrameRateLimitPresent = cap;

		this.deadline += period;
		this.presented = true;
	}

	private void PresentWithoutLimit()
	{
		Device* device = Device.Instance();
		uint preset = device->FrameRateLimitPresetPresent;
		ushort cap = device->FrameRateLimitPresent;
		device->FrameRateLimitPresent = 0;
		device->FrameRateLimitPresetPresent = this.presentingSyncInterval;
		this.presentHook!.Original(this.presenting);
		device->FrameRateLimitPresetPresent = preset;
		device->FrameRateLimitPresent = cap;
	}

	/// <summary>
	/// Blocks until the next Present restores the back buffer, at most 1 s. Required before Dispose. Not on the framework
	/// thread, which runs that Present
	/// </summary>
	public void StopPresenter()
	{
		this.stopping = true;
		for (int i = 0; i < 100 && this.presenter.Active; i++)
			Thread.Sleep(10);
	}

	private bool ReadVBlank()
	{
		DWM_TIMING_INFO info = default;
		info.cbSize = (uint)sizeof(DWM_TIMING_INFO);
		if (!DwmGetCompositionTimingInfo(default, &info).SUCCEEDED)
			return false;

		this.vblank = (long)info.qpcVBlank;
		this.vblankPeriod = (long)info.qpcRefreshPeriod;
		return this.vblankPeriod > 0;
	}

	/// <summary>
	/// Returns the first time at or after this one when a Present issued at that time reaches the screen at the next
	/// vblank.
	/// </summary>
	private long AlignToVBlank(long time)
	{
		if (!this.ReadVBlank())
			return time;

		long next = this.vblank + ((time - this.vblank) / this.vblankPeriod + 1) * this.vblankPeriod;
		long aligned = next - VBlankLead;
		return aligned >= time ? aligned : aligned + this.vblankPeriod;
	}

	private void TrackHeadroom(bool missed)
	{
		if (this.late[this.lateIndex])
			this.lateCount--;
		this.late[this.lateIndex] = missed;
		if (missed)
			this.lateCount++;
		this.lateIndex = (this.lateIndex + 1) % this.late.Length;

		if (this.KeepingUp && this.lateCount > this.late.Length / 20)
			this.KeepingUp = false;
		else if (!this.KeepingUp && this.lateCount <= this.late.Length / 100)
			this.KeepingUp = true;
	}

	private void ResetHeadroom()
	{
		Array.Clear(this.late);
		Array.Clear(this.halves);
		this.lateCount = 0;
		this.lateIndex = 0;
		this.KeepingUp = true;
	}

	/// <summary>
	/// Returns half the target. The target is the refresh rate unless the configuration sets it lower. The display
	/// shows frames at twice this rate. The game's limit does not apply.
	/// </summary>
	private int GeneratedRate(Device* device)
	{
		int refresh = device->FrameRate;
		int target = this.configuration.FrameGenerationTarget;
		return (target > 0 && target < refresh ? target : refresh) / 2;
	}

	/// <summary>Returns the 60/30 fps option only when it is below the refresh rate, like the game's limiter</summary>
	private int LimitedRate(Device* device)
	{
		if (!this.configuration.LowLatencyLimiter || device->FrameRateLimitPresetPresent != 1)
			return 0;

		int cap = device->FrameRateLimitPresent;
		return cap != 0 && cap < device->FrameRate ? cap : 0;
	}

	/// <summary>
	/// Presents the generated frame (halfway from the previous frame to this one) first, and this frame half a frame
	/// later. When the runner generates nothing, it presents this frame again. The pacer times the second Present
	/// because of two Presents queued back to back:
	/// - The blt swapchain does not hold the second Present for the next vblank.
	/// - The compositor only shows the newer one.
	/// When effects is true, the frame already contains ReShade's effects. Neither Present runs ReShade's pass.
	/// While the presenter's NeedsProbe is true, the second Present is the presenter's probe.
	/// </summary>
	/// <returns>How long each Present call took, ours and whatever else runs inside it</returns>
	private (long First, long Second) PresentTwice(Device* device, SwapChain* swapChain, long period, uint syncInterval, bool effects)
	{
		ID3D11DeviceContext* context = (ID3D11DeviceContext*)device->D3D11DeviceContext;
		ID3D11Texture2D* backBuffer = (ID3D11Texture2D*)swapChain->BackBuffer->D3D11Texture2D;
		this.Hold(context, backBuffer);

		bool probe = this.presenter.NeedsProbe(swapChain);
		int draws = probe ? this.presenter.StartProbe() : 0;
		long start = Stopwatch.GetTimestamp();
		context->CopyResource((ID3D11Resource*)this.held, (ID3D11Resource*)backBuffer);
		this.uiLayer.CopyRegenerated(swapChain);
		this.presentHook!.Original(swapChain);
		long first = Stopwatch.GetTimestamp() - start;

		context->CopyResource((ID3D11Resource*)backBuffer, (ID3D11Resource*)this.held);
		this.timer.Wait(this.deadline + period / 2);
		if (effects)
			this.reShade.SkipNextPresent();
		start = Stopwatch.GetTimestamp();
		if (probe)
			this.presenter.FinishProbe(swapChain, syncInterval, draws);
		else
			this.presentHook!.Original(swapChain);
		return (first, Stopwatch.GetTimestamp() - start);
	}

	private void Hold(ID3D11DeviceContext* context, ID3D11Texture2D* backBuffer)
	{
		D3D11_TEXTURE2D_DESC desc = Dx.GetDesc(backBuffer);
		if (this.held != null)
		{
			D3D11_TEXTURE2D_DESC heldDesc = Dx.GetDesc(this.held);
			if (heldDesc.Width == desc.Width && heldDesc.Height == desc.Height && heldDesc.Format == desc.Format)
				return;

			Dx.Release(this.held);
			this.held = null;
		}

		ID3D11Device* d3dDevice;
		context->GetDevice(&d3dDevice);
		this.held = Dx.CreateTexture(d3dDevice, desc.Width, desc.Height, desc.Format, 1, 0);
		d3dDevice->Release();
	}

	private long PredictedWork() => this.work.Max() + Margin;

	public void Dispose()
	{
		this.tickHook?.Dispose();
		this.presentHook?.Dispose();
		this.presenter.Dispose();
		this.timer.Dispose();
		Dx.Release(this.held);
		this.held = null;
		GC.SuppressFinalize(this);
	}
}
