using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;

using Dalamud.Hooking;

using FFXIVClientStructs.FFXIV.Client.Graphics.Kernel;

using TerraFX.Interop.DirectX;

using Framework = FFXIVClientStructs.FFXIV.Client.System.Framework.Framework;

namespace UpscaleBuddy.Game;

/// <summary>
/// Replaces the game's frame limiter. The game sleeps inside SwapChain.Present after the frame is built, so what
/// reaches the screen is a whole sleep old. Here the sleep runs after Framework.Tick, before the next message pump and
/// input read, sized so the frame is done just in time for an absolute Present schedule.
/// Frame generation: base rate refresh/2, the generated frame shown first and the real one half a frame later. They go to the
/// present thread, so the game's frame work gets a whole rendered frame; when that can't run the main thread presents both.
/// </summary>
public unsafe class FramePacer: IDisposable
{
	// Sleep until 2 ms before, then spin
	private static readonly long SpinTicks = Stopwatch.Frequency / 500;

	// Start the frame this much before the slowest recent one would need
	private static readonly long Margin = Stopwatch.Frequency / 2000;

	private delegate void PresentDelegate(SwapChain* swapChain);

	private readonly UpscaleBuddyConfiguration configuration;
	private readonly UiLayer uiLayer;
	private readonly RenderFrameHook renderFrames;
	private readonly PresentThread presenter;
	private readonly Action noopPresent;
	private nint noopSwapChain;
	private volatile bool stopping;
	private readonly Hook<Framework.Delegates.Tick>? tickHook;
	private readonly Hook<PresentDelegate>? presentHook;

	// Main thread, tick start to Present of the last frames
	private readonly long[] work = new long[32];
	private int workIndex;

	// Frame generation: the longer half of each of the last frames, first Present or this frame's work + second Present
	private readonly long[] halves = new long[32];
	private int halfIndex;
	private long tickStart;
	private long deadline;
	private bool presented;

	// Back buffer content for the second Present, the discard swap effect leaves it undefined
	private ID3D11Texture2D* held;

	// Frame generation headroom: which of the last frames missed their slot
	private static readonly long LateSlack = Stopwatch.Frequency / 1000;
	private readonly bool[] late = new bool[120];
	private int lateIndex;
	private int lateCount;

	public FramePacer(UpscaleBuddyConfiguration configuration, UiLayer uiLayer, RenderFrameHook renderFrames)
	{
		this.configuration = configuration;
		this.uiLayer = uiLayer;
		this.renderFrames = renderFrames;
		this.presenter = new PresentThread(uiLayer);
		renderFrames.Started += this.presenter.Started;
		renderFrames.Finished += this.presenter.Publish;
		this.noopPresent = () => this.CallOriginal((SwapChain*)this.noopSwapChain);
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
	/// Frame generation keeps up with the target: false once over 5% of the last 120 frames missed their slot, true again
	/// under 1%, so it doesn't flicker
	/// </summary>
	public bool KeepingUp { get; private set; } = true;

	/// <summary>
	/// The target the recent frames would hold, from the slowest of the last frames: shown at twice the rate one frame's work
	/// allows. With the present thread that's the frame's work; when the main thread presents both frames, half a rendered
	/// frame each: the first Present, then the next frame's work and the second Present
	/// </summary>
	public int SustainedTarget
	{
		get
		{
			long slowest = this.halves.Max();
			return slowest > 0 ? (int)(Stopwatch.Frequency / slowest) & ~1 : 0;
		}
	}

	/// <summary>Frame generation's presents run on the present thread; false when the game's thread makes them</summary>
	public bool PresentsOnThread => this.presenter.Active;

	/// <summary>The display's refresh rate, 0 when unknown</summary>
	public int RefreshRate
	{
		get
		{
			Device* device = Device.Instance();
			return device != null ? device->FrameRate : 0;
		}
	}

	/// <summary>Frame generation's rendered rate (shown at twice that), 0 when off or the refresh rate is unknown</summary>
	public int GenerationRate
	{
		get
		{
			Device* device = Device.Instance();
			return this.configuration.FrameGeneration && device != null ? this.GeneratedRate(device) : 0;
		}
	}

	private bool TickDetour(Framework* framework)
	{
		bool result = this.tickHook!.Original(framework);
		if (this.presented)
		{
			this.presented = false;
			Wait(this.deadline - this.PredictedWork());
		}

		this.tickStart = Stopwatch.GetTimestamp();
		return result;
	}

	/// <summary>The game's Present, with Dalamud's detour inside</summary>
	private void CallOriginal(SwapChain* swapChain) => this.presentHook!.Original(swapChain);

	private void PresentDetour(SwapChain* swapChain)
	{
		Device* device = Device.Instance();
		int generated = this.configuration.FrameGeneration ? this.GeneratedRate(device) : 0;
		bool generate = generated != 0;
		bool threaded = generate && !this.stopping;

		// The game's own Present needs the game's back buffer texture, on every path below
		if (!threaded)
		{
			this.presenter.ResetFailure();
			if (this.presenter.Active)
				this.presenter.Release();
		}

		int rate = generate ? generated : this.LimitedRate(device);
		if (swapChain != device->SwapChain || rate == 0)
		{
			this.deadline = 0;
			this.ResetHeadroom();
			this.CallOriginal(swapChain);
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
			this.TrackHeadroom(this.deadline != 0 && now > this.deadline + LateSlack);
		else
			this.ResetHeadroom();

		// First frame or over a frame behind
		if (this.deadline == 0 || now - this.deadline > period)
			this.deadline = now;

		if (threaded && this.PresentOnThread(device, swapChain, period, frameWork))
			return;

		Wait(this.deadline);

		// Cap 0 skips the game's sleep, preset 1 is sync interval 1
		uint preset = device->FrameRateLimitPresetPresent;
		ushort cap = device->FrameRateLimitPresent;
		device->FrameRateLimitPresent = 0;
		if (generate)
		{
			device->FrameRateLimitPresetPresent = 1;
			(long first, long second) = this.PresentTwice(device, swapChain, period);
			this.halves[this.halfIndex] = Math.Max(first, frameWork + second);
			this.halfIndex = (this.halfIndex + 1) % this.halves.Length;
		}
		else
		{
			this.CallOriginal(swapChain);
		}

		device->FrameRateLimitPresetPresent = preset;
		device->FrameRateLimitPresent = cap;

		this.deadline += period;
		this.presented = true;
	}

	/// <summary>
	/// The render thread publishes the frame it kicks here to the present thread, which shows the generated one a render time
	/// after the deadline and this one half a rendered frame later. The game's own Present then only runs for Dalamud, which draws
	/// on the main thread while the render thread is idle (its plugins' Draw callbacks have to stay on it), so the work here has
	/// the whole rendered frame
	/// </summary>
	private bool PresentOnThread(Device* device, SwapChain* swapChain, long period, long frameWork)
	{
		uint preset = device->FrameRateLimitPresetPresent;
		if (!this.presenter.Prepare(device, swapChain, this.deadline, period, preset != 0))
			return false;

		ushort cap = device->FrameRateLimitPresent;
		device->FrameRateLimitPresent = 0;
		device->FrameRateLimitPresetPresent = 0;
		this.noopSwapChain = (nint)swapChain;
		this.presenter.NoopPresent(this.noopPresent);
		device->FrameRateLimitPresetPresent = preset;
		device->FrameRateLimitPresent = cap;

		// The estimate's unit is half a rendered frame
		this.halves[this.halfIndex] = frameWork / 2;
		this.halfIndex = (this.halfIndex + 1) % this.halves.Length;
		this.deadline += period;
		this.presented = true;
		return true;
	}

	/// <summary>Framework thread: the present thread and the back buffer texture are handed back at the next Present</summary>
	public void Stop() => this.stopping = true;

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

	/// <summary>Half the target (the refresh rate unless set lower), frames are shown at twice this; the game's limit doesn't apply</summary>
	private int GeneratedRate(Device* device)
	{
		int refresh = device->FrameRate;
		int target = this.configuration.FrameGenerationTarget;
		return (target > 0 && target < refresh ? target : refresh) / 2;
	}

	/// <summary>Options 60/30 fps, only when below the refresh rate like the game's own limiter</summary>
	private int LimitedRate(Device* device)
	{
		if (!this.configuration.LowLatencyLimiter || device->FrameRateLimitPresetPresent != 1)
			return 0;

		int cap = device->FrameRateLimitPresent;
		return cap != 0 && cap < device->FrameRate ? cap : 0;
	}

	/// <summary>
	/// The generated frame (halfway from the previous frame to this one) first, this frame half a frame later; the frame
	/// again when nothing was generated. Queued back to back, the blt swapchain doesn't hold the second for the next vblank
	/// and the compositor only shows the newer one, so the second Present is timed
	/// </summary>
	/// <returns>How long each Present call took, ours and whatever else runs inside it</returns>
	private (long First, long Second) PresentTwice(Device* device, SwapChain* swapChain, long period)
	{
		ID3D11DeviceContext* context = (ID3D11DeviceContext*)device->D3D11DeviceContext;
		ID3D11Texture2D* backBuffer = (ID3D11Texture2D*)swapChain->BackBuffer->D3D11Texture2D;
		this.Hold(context, backBuffer);

		long start = Stopwatch.GetTimestamp();
		context->CopyResource((ID3D11Resource*)this.held, (ID3D11Resource*)backBuffer);
		this.uiLayer.CopyRegenerated(swapChain);
		this.CallOriginal(swapChain);
		long first = Stopwatch.GetTimestamp() - start;

		context->CopyResource((ID3D11Resource*)backBuffer, (ID3D11Resource*)this.held);
		Wait(this.deadline + period / 2);
		start = Stopwatch.GetTimestamp();
		this.CallOriginal(swapChain);
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

	internal static void Wait(long until)
	{
		long now = Stopwatch.GetTimestamp();
		while (until - now > SpinTicks)
		{
			Thread.Sleep(1);
			now = Stopwatch.GetTimestamp();
		}

		while (now < until)
		{
			Thread.SpinWait(16);
			now = Stopwatch.GetTimestamp();
		}
	}

	public void Dispose()
	{
		this.renderFrames.Started -= this.presenter.Started;
		this.renderFrames.Finished -= this.presenter.Publish;
		this.tickHook?.Dispose();
		this.presentHook?.Dispose();
		this.presenter.Dispose();
		Dx.Release(this.held);
		this.held = null;
		GC.SuppressFinalize(this);
	}
}
