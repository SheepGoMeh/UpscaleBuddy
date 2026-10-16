using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

using FFXIVClientStructs.FFXIV.Client.Graphics.Kernel;

using TerraFX.Interop.DirectX;

using UpscaleBuddy.FrameGeneration;

using static TerraFX.Interop.Windows.Windows;

using KernelTexture = FFXIVClientStructs.FFXIV.Client.Graphics.Kernel.Texture;

namespace UpscaleBuddy.Game;

/// <summary>
/// Frame generation's presents on a thread of their own, so the game's frame work isn't squeezed into half a rendered frame
/// around two Present calls. Needs the render thread idle when it starts, which PostTick gives between "render done" and the
/// kick of the next frame.
/// - The back buffer's Kernel texture gets our D3D11 texture and render target view in place (Texture+0x68, +0x80[0]): the game
///   draws its final output, clears and screenshots there, and the swap chain's buffer is only touched by the present thread.
/// - The render thread publishes each finished frame itself (<see cref="Publish"/>, right after the frame's last command): the
///   real frame and the generated one go into a ring of their own, and their two presents are queued at times worked out from
///   the frame's kick deadline and how long the render thread recently took, not from when the main thread gets to Present.
///   The main thread only tells the pacer's deadline and the rendered period at its Present.
/// - With ToneAdjust off, or only a gamma, the published frames are the scene alone (real and generated) and the frame's layers
///   come with them, kept in rings (<see cref="UiLayer"/>): the UI never becomes part of a generated frame, the present thread
///   lays the frame's nameplates, then the UI, then the overlay over whatever it shows, pixel for pixel and at the output size.
///   The UI layer advances at most once per UI period, 60 Hz, it was made for that. With a colour filter the frames carry the UI
///   as the game made it.
/// - Present thread: CopyResource into the swap chain's buffer, the layers over it, and the present, under
///   ID3D11Multithread protection of the game's immediate context; they are drawn in a state of their own, so the render
///   thread's state stays as it was.
/// - Dalamud draws inside IDXGISwapChain::Present, which Present1 isn't: the main thread still makes one Present call per real
///   frame (away from the present thread's slots) with the buffer holding the frame on screen, so it shows nothing new. That
///   draw stays on the main thread, at this point: it sets pipeline state on the game's immediate context, which the render
///   thread is using unless it's idle, and plugins' Draw callbacks read game state like the ObjectTable that only the main
///   thread may.
/// - Its overlay goes into a transparent texture of ours (<see cref="OverlayCapture"/>), one of a ring of them: the present thread
///   lays the newest finished one over every frame it shows, so no frame waits for it and the draw never touches a texture the
///   present thread is reading.
/// - The game's resize callbacks (Device.OnResizeDestroy / OnResizeCreate) hand the original texture back before it's released.
/// </summary>
internal sealed unsafe class PresentThread: IDisposable
{
	private const int Ring = 4;
	private const int OverlayRing = 3;
	private const int QueueSize = 16;
	private static readonly long Millisecond = Stopwatch.Frequency / 1000;

	// The UI was made for 60 Hz; a UI layer is only swapped for a newer one that often (a little early is fine, frames jitter)
	private static readonly long UiPeriod = Stopwatch.Frequency / 60;
	private static readonly long UiTolerance = Millisecond;

	/// <summary>What was published with a frame's two textures: the layers to lay over them</summary>
	private struct FrameRecord
	{
		public bool Layered;
		public KernelTexture* Ui;
		public KernelTexture* Plates;

		// ToneAdjust's exponent for the presenter to apply, 0 when there is none
		public float Gamma;

		// The generated frame while the generator is still making it; the present thread takes it, once
		public GenerationTicket? Ticket;
	}

	private struct Entry
	{
		public long Due;
		public long Frame; // Which published frame, its ring slot is reused after Ring more
		public int Texture; // ring * 2 + 1 for the real frame, ring * 2 for the generated one
	}

	private static PresentThread? current;

	private readonly UiLayer uiLayer;
	private readonly object presentLock = new();
	private readonly object queueLock = new();
	private readonly Entry[] queue = new Entry[QueueSize];
	private int queueHead;
	private int queueCount;

	// Main thread
	private volatile bool active;
	private ID3D11Device* device;
	private ID3D11DeviceContext* context;
	private IDXGISwapChain1* swapChain;
	private IDXGIOutput* output;

	// The game's vsync setting: a frame is shown right after a vblank, so its flip can't tear mid image
	private volatile bool vsync;
	private ID3D11Texture2D* buffer;
	private ID3D11RenderTargetView* bufferView;
	private KernelTexture* backBuffer;
	private void* originalTexture;
	private void* originalView;
	private ID3D11Texture2D* ours;
	private ID3D11RenderTargetView* oursView;
	private ID3D11Texture2D* composed;
	private ID3D11RenderTargetView* composedView;
	private ID3D11ShaderResourceView* composedResource;
	private uint width;
	private uint height;

	// Published by the render thread, which is the only writer
	private long published;
	private readonly ID3D11Texture2D*[] real = new ID3D11Texture2D*[Ring];
	private readonly ID3D11RenderTargetView*[] realView = new ID3D11RenderTargetView*[Ring];
	private readonly ID3D11Texture2D*[] generated = new ID3D11Texture2D*[Ring];
	private readonly ID3D11RenderTargetView*[] generatedView = new ID3D11RenderTargetView*[Ring];
	private readonly FrameRecord[] records = new FrameRecord[Ring];

	// Under the present lock: the frame whose UI layer is on screen, when the next one is due, and the last presented entry
	private long shownUiFrame = -1;
	private long nextUiDue;
	private long lastShownFrame = -1;

	// Dalamud's overlay, premultiplied alpha, in a ring: drawn into the next one on the main thread, laid over every frame shown
	private readonly OverlayCapture overlay = new();
	private readonly ID3D11Texture2D*[] layer = new ID3D11Texture2D*[OverlayRing];
	private readonly ID3D11RenderTargetView*[] layerView = new ID3D11RenderTargetView*[OverlayRing];
	private readonly ID3D11ShaderResourceView*[] layerResource = new ID3D11ShaderResourceView*[OverlayRing];
	private int overlayDrawn;
	private int overlayFinished = -1;

	// The pacer's deadline of the frame about to be kicked and the rendered period, set by the main thread before the kick
	private long kickDeadline;
	private long period;

	// Render thread: the frame being rendered, and how long the last ones took
	private long frameKick;
	private long frameDeadline;
	private readonly long[] renderTimes = new long[32];
	private int renderIndex;
	private long lastDue;
	private long generationLag;

	private int destroyCallback = -1;
	private int createCallback = -1;
	private long noopCost = Millisecond;

	private Thread? thread;
	private volatile bool running;
	private volatile int lastShown = -1;
	private bool failed;

	public PresentThread(UiLayer uiLayer)
	{
		this.uiLayer = uiLayer;
	}

	public bool Active => this.active;

	/// <summary>A start that failed isn't retried every frame; switching the option off and on tries again</summary>
	public void ResetFailure() => this.failed = false;

	/// <summary>
	/// Main thread at Present, render thread idle: the thread is started when it isn't running, and the pacer's deadline of the
	/// frame about to be kicked and the rendered period are handed over for the render thread to work its presents out from.
	/// False when the thread can't run here and the game's own Present should do it
	/// </summary>
	/// <param name="deadline">When the frame about to be kicked is meant to be done</param>
	/// <param name="period">One rendered frame</param>
	/// <param name="vsync">Whether the game's frame rate setting syncs to the display</param>
	public bool Prepare(Device* kernelDevice, SwapChain* kernelSwapChain, long deadline, long period, bool vsync)
	{
		this.vsync = vsync;
		if (this.active && (kernelSwapChain->BackBuffer != this.backBuffer || kernelSwapChain->BackBuffer->ActualWidth != this.width
			|| kernelSwapChain->BackBuffer->ActualHeight != this.height))
			this.Release();

		if (!this.active && (this.failed || !this.Start(kernelDevice, kernelSwapChain)))
			return false;

		this.kickDeadline = deadline;
		this.period = period;
		return true;
	}

	/// <summary>Render thread, the kick: the frame being rendered belongs to the deadline the main thread just handed over</summary>
	public void Started(long kick)
	{
		this.frameKick = kick;
		this.frameDeadline = this.kickDeadline;
	}

	/// <summary>
	/// Render thread, right after the frame's last command, while the main thread waits for it: the game's final output (the
	/// back buffer's texture is ours) and the generated frame (the real one again when there's none) go into a ring slot, and
	/// the frame's two presents are queued. The generated frame is due once this frame will surely be done, by the worst of the
	/// recent render times after its deadline, the real one half a rendered frame later
	/// </summary>
	public void Publish(long done)
	{
		if (!this.active)
			return;

		long count = this.published;
		int slot = (int)(count % Ring);
		this.records[slot] = this.Fill(slot);
		Volatile.Write(ref this.published, count + 1);
		this.Schedule(count, done, Stopwatch.GetTimestamp());
	}

	/// <summary>
	/// The frame's two textures: the scene alone with its layers recorded when the frame was made layered, otherwise the game's
	/// final output and the regenerated frame (the real one again when there's none), which carry the UI
	/// </summary>
	private FrameRecord Fill(int slot)
	{
		if (this.uiLayer.TakeOutput(out UiLayer.FrameOutput output) && output.Layered)
		{
			this.uiLayer.Blit(this.context, this.realView[slot], this.width, this.height, (ID3D11ShaderResourceView*)output.Real->D3D11ShaderResourceView);
			if (output.Generated != null)
				this.uiLayer.Blit(this.context, this.generatedView[slot], this.width, this.height, (ID3D11ShaderResourceView*)output.Generated->D3D11ShaderResourceView);
			return new FrameRecord { Layered = true, Ui = output.Ui, Plates = output.Plates, Gamma = output.Gamma, Ticket = output.Ticket };
		}

		this.context->CopyResource((ID3D11Resource*)this.real[slot], (ID3D11Resource*)this.ours);
		KernelTexture* generatedFrame = this.uiLayer.Regenerated;
		ID3D11ShaderResourceView* source = generatedFrame != null ? (ID3D11ShaderResourceView*)generatedFrame->D3D11ShaderResourceView : null;
		if (source != null)
			this.uiLayer.Blit(this.context, this.generatedView[slot], this.width, this.height, source);
		else
			this.context->CopyResource((ID3D11Resource*)this.generated[slot], (ID3D11Resource*)this.ours);
		return default;
	}

	private void Schedule(long frame, long done, long now)
	{
		int slot = (int)(frame % Ring);
		this.renderTimes[this.renderIndex] = Math.Max(0, done - this.frameKick);
		this.renderIndex = (this.renderIndex + 1) % this.renderTimes.Length;
		long worst = 0;
		foreach (long time in this.renderTimes)
			worst = Math.Max(worst, time);

		long half = this.period / 2;
		long lag = Volatile.Read(ref this.generationLag);
		long generatedDue = Math.Max(Math.Max(this.frameDeadline + worst + Millisecond + lag, now + Millisecond / 2), this.lastDue + (Millisecond * 2));
		this.lastDue = generatedDue + half;
		lock (this.queueLock)
		{
			while (this.queueCount > QueueSize - 2)
			{
				this.queueHead = (this.queueHead + 1) % QueueSize;
				this.queueCount--;
			}

			this.Enqueue(new Entry { Due = generatedDue, Frame = frame, Texture = slot * 2 });
			this.Enqueue(new Entry { Due = this.lastDue, Frame = frame, Texture = (slot * 2) + 1 });
		}
	}

	private void Enqueue(Entry entry)
	{
		this.queue[(this.queueHead + this.queueCount) % QueueSize] = entry;
		this.queueCount++;
	}

	/// <summary>
	/// Main thread, after <see cref="Prepare"/>: the game's own Present, for Dalamud's sake. The buffer holds what's on screen first, and
	/// it's kept away from the present thread's slots. Dalamud's overlay goes into the next texture of the ring instead of the
	/// buffer, and the present thread lays it over the frames it shows from then on
	/// </summary>
	public void NoopPresent(Action present)
	{
		long start = Stopwatch.GetTimestamp();
		long window = this.noopCost + (Millisecond / 2);
		while (true)
		{
			long now = Stopwatch.GetTimestamp();
			long next = this.NextDue();
			if (next == 0 || next - now > window || now - next > Millisecond / 2 || now - start > Millisecond * 3)
				break;

			Thread.SpinWait(64);
		}

		int drawing = (this.overlayDrawn + 1) % OverlayRing;
		float* transparent = stackalloc float[4] { 0, 0, 0, 0 };
		this.context->ClearRenderTargetView(this.layerView[drawing], transparent);
		this.overlay.Arm(this.context, this.layerView[drawing]);
		lock (this.presentLock)
		{
			if (this.lastShown >= 0)
			{
				(int texture, long frame) = this.FrameToReshow();
				this.Show(this.Texture(texture), frame);
			}
			else
			{
				this.context->CopyResource((ID3D11Resource*)this.buffer, (ID3D11Resource*)this.OnScreen());
				this.CompositeOverlay();
			}

			long begin = Stopwatch.GetTimestamp();
			present();
			long cost = Stopwatch.GetTimestamp() - begin;
			this.noopCost = ((this.noopCost * 3) + cost) / 4;
		}

		this.overlay.Disarm();
		this.overlayDrawn = drawing;
		Volatile.Write(ref this.overlayFinished, drawing);
	}

	/// <summary>
	/// What the main thread's Present shows again: the frame on screen, or the newest real frame once the rings have reused that
	/// one's slots (the present thread fell behind, a frame's layers and textures are gone then). Showing the frame without its
	/// record drew the scene without the UI
	/// </summary>
	private (int Texture, long Frame) FrameToReshow()
	{
		long published = Volatile.Read(ref this.published);
		if (published - this.lastShownFrame < Ring)
			return (this.lastShown, this.lastShownFrame);

		long newest = published - 1;
		return (((int)(newest % Ring) * 2) + 1, newest);
	}

	/// <summary>The frame on screen, or the newest published one when the thread hasn't shown anything yet</summary>
	private ID3D11Texture2D* OnScreen()
	{
		int shown = this.lastShown;
		if (shown >= 0)
			return this.Texture(shown);

		long count = Volatile.Read(ref this.published);
		return count > 0 ? this.real[(count - 1) % Ring] : this.ours;
	}

	private ID3D11Texture2D* Texture(int code) => (code & 1) != 0 ? this.real[code / 2] : this.generated[code / 2];

	/// <summary>
	/// A published frame's real or generated texture onto the back buffer, in the game's order: its layers over it, the same for
	/// either and as they are (the nameplates, then the UI at its own pace), ToneAdjust's gamma over all of that when the game has
	/// it on, then the overlay, which the game draws after ToneAdjust
	/// </summary>
	private void Show(ID3D11Texture2D* source, long frame)
	{
		long published = Volatile.Read(ref this.published);
		FrameRecord record = published - frame < Ring ? this.records[(int)(frame % Ring)] : default;
		if (!record.Layered)
		{
			this.context->CopyResource((ID3D11Resource*)this.buffer, (ID3D11Resource*)source);
			this.CompositeOverlay();
			return;
		}

		// ToneAdjust reads the composed frame, so that is a texture of its own
		bool tone = record.Gamma > 0;
		ID3D11RenderTargetView* view = tone ? this.composedView : this.bufferView;
		this.context->CopyResource((ID3D11Resource*)(tone ? this.composed : this.buffer), (ID3D11Resource*)source);
		if (record.Plates != null)
			this.uiLayer.BlendOver(this.context, view, this.width, this.height, (ID3D11ShaderResourceView*)record.Plates->D3D11ShaderResourceView);

		KernelTexture* ui = this.PacedUi(frame, published);
		if (ui != null)
			this.uiLayer.BlendOver(this.context, view, this.width, this.height, (ID3D11ShaderResourceView*)ui->D3D11ShaderResourceView);
		if (tone)
			this.uiLayer.Tone(this.context, this.bufferView, this.width, this.height, this.composedResource, record.Gamma);

		this.CompositeOverlay();
	}

	/// <summary>
	/// The UI layer to show with a frame: the one on screen until the UI's next 60 Hz tick, then the newest, so a game rendering
	/// faster than 60 doesn't speed the UI up; a game rendering slower gets a new one with every frame
	/// </summary>
	private KernelTexture* PacedUi(long frame, long published)
	{
		long now = Stopwatch.GetTimestamp();
		bool gone = this.shownUiFrame < 0 || published - this.shownUiFrame >= Ring;
		if (gone || (frame > this.shownUiFrame && now >= this.nextUiDue - UiTolerance))
		{
			this.shownUiFrame = frame;
			this.nextUiDue = this.nextUiDue == 0 || now - this.nextUiDue > UiPeriod ? now + UiPeriod : this.nextUiDue + UiPeriod;
		}

		FrameRecord shown = this.records[this.shownUiFrame % Ring];
		return shown.Layered ? shown.Ui : null;
	}

	/// <summary>The newest finished overlay over the back buffer, in a state of its own</summary>
	private void CompositeOverlay()
	{
		int finished = Volatile.Read(ref this.overlayFinished);
		if (finished >= 0)
			this.uiLayer.BlendOver(this.context, this.bufferView, this.width, this.height, this.layerResource[finished]);
	}

	private long NextDue()
	{
		lock (this.queueLock)
			return this.queueCount > 0 ? this.queue[this.queueHead].Due : 0;
	}

	/// <summary>Main thread, render thread idle: our texture goes into the game's back buffer, the thread starts</summary>
	private bool Start(Device* kernelDevice, SwapChain* kernelSwapChain)
	{
		KernelTexture* back = kernelSwapChain->BackBuffer;
		if (back == null || kernelSwapChain->DXGISwapChain == null || back->MipRenderTargets == null || back->D3D11Texture2D == null)
			return false;

		try
		{
			this.swapChain = Dx.Query<IDXGISwapChain1>(kernelSwapChain->DXGISwapChain);
			IDXGIOutput* containing;
			if (this.swapChain->GetContainingOutput(&containing).SUCCEEDED)
				this.output = containing;
			ID3D11Texture2D* swapChainBuffer;
			ThrowIfFailed(this.swapChain->GetBuffer(0, __uuidof<ID3D11Texture2D>(), (void**)&swapChainBuffer));
			this.buffer = swapChainBuffer;
			if ((void*)this.buffer != back->D3D11Texture2D || back->MipRenderTargets[0].D3D11RenderTargetViewOrDepthStencilView == null)
			{
				Service.PluginLog.Warning("The back buffer's texture isn't the swap chain's buffer, no present thread");
				this.Release();
				this.failed = true;
				return false;
			}

			ID3D11Device* created;
			this.buffer->GetDevice(&created);
			this.device = created;
			ID3D11DeviceContext* immediate;
			this.device->GetImmediateContext(&immediate);
			this.context = immediate;

			// The game's context is used from the render thread while we copy and present
			ID3D11Multithread* multithread = Dx.Query<ID3D11Multithread>(this.context);
			multithread->SetMultithreadProtected(true);
			multithread->Release();

			D3D11_TEXTURE2D_DESC desc = Dx.GetDesc(this.buffer);
			this.width = desc.Width;
			this.height = desc.Height;
			uint bind = (uint)(D3D11_BIND_FLAG.D3D11_BIND_RENDER_TARGET | D3D11_BIND_FLAG.D3D11_BIND_SHADER_RESOURCE);
			this.ours = Dx.CreateTexture(this.device, desc.Width, desc.Height, desc.Format, 1, bind);
			ThrowIfFailed(this.CreateView(this.ours, out this.oursView));
			ThrowIfFailed(this.CreateView(this.buffer, out this.bufferView));
			this.composed = Dx.CreateTexture(this.device, desc.Width, desc.Height, desc.Format, 1, bind);
			ThrowIfFailed(this.CreateView(this.composed, out this.composedView));
			this.composedResource = Dx.CreateSrv(this.device, this.composed);
			uint target = (uint)D3D11_BIND_FLAG.D3D11_BIND_RENDER_TARGET;
			for (int i = 0; i < Ring; i++)
			{
				this.real[i] = Dx.CreateTexture(this.device, desc.Width, desc.Height, desc.Format, 1, target);
				ThrowIfFailed(this.CreateView(this.real[i], out this.realView[i]));
				this.generated[i] = Dx.CreateTexture(this.device, desc.Width, desc.Height, desc.Format, 1, target);
				ThrowIfFailed(this.CreateView(this.generated[i], out this.generatedView[i]));
			}

			for (int i = 0; i < OverlayRing; i++)
			{
				this.layer[i] = Dx.CreateTexture(this.device, desc.Width, desc.Height, desc.Format, 1, bind);
				ThrowIfFailed(this.CreateView(this.layer[i], out this.layerView[i]));
				this.layerResource[i] = Dx.CreateSrv(this.device, this.layer[i]);
			}

			// The frame the game just finished is in the swap chain's buffer, our texture takes over from the next one
			this.context->CopyResource((ID3D11Resource*)this.ours, (ID3D11Resource*)this.buffer);
			this.backBuffer = back;
			this.originalTexture = back->D3D11Texture2D;
			this.originalView = back->MipRenderTargets[0].D3D11RenderTargetViewOrDepthStencilView;
			back->D3D11Texture2D = this.ours;
			back->MipRenderTargets[0].D3D11RenderTargetViewOrDepthStencilView = this.oursView;
		}
		catch (Exception e)
		{
			Service.PluginLog.Error(e, "Present thread couldn't start");
			this.Release();
			this.failed = true;
			return false;
		}

		current = this;
		this.RegisterResizeCallbacks(kernelDevice);
		this.published = 0;
		this.lastDue = 0;
		this.generationLag = 0;
		this.lastShown = -1;
		this.lastShownFrame = -1;
		this.shownUiFrame = -1;
		this.nextUiDue = 0;
		Array.Clear(this.records);
		this.overlayDrawn = 0;
		this.overlayFinished = -1;
		this.queueHead = 0;
		this.queueCount = 0;
		Array.Clear(this.renderTimes);
		this.running = true;
		this.active = true;
		this.uiLayer.LayersAtPresent = true;
		this.thread = new Thread(this.Run) { Name = "UpscaleBuddy present", IsBackground = true, Priority = ThreadPriority.Highest };
		this.thread.Start();
		return true;
	}

	private int CreateView(ID3D11Texture2D* texture, out ID3D11RenderTargetView* view)
	{
		ID3D11RenderTargetView* created;
		int result = this.device->CreateRenderTargetView((ID3D11Resource*)texture, null, &created);
		view = created;
		return result;
	}

	private void Run()
	{
		DXGI_PRESENT_PARAMETERS parameters = default;
		while (this.running)
		{
			Entry entry;
			lock (this.queueLock)
			{
				entry = this.queueCount == 0 ? default : this.queue[this.queueHead];
			}

			if (entry.Due == 0)
			{
				Thread.Sleep(1);
				continue;
			}

			FramePacer.Wait(entry.Due);
			if (!this.running)
				break;

			long late = Stopwatch.GetTimestamp() - entry.Due;
			lock (this.queueLock)
			{
				if (this.queueCount > 0 && this.queue[this.queueHead].Due == entry.Due)
				{
					this.queueHead = (this.queueHead + 1) % QueueSize;
					this.queueCount--;
				}
			}

			// Woke far too late (or the frame was): presenting right behind the previous one would only look worse. A frame whose ring
			// slot the render thread is about to reuse is gone
			// The vblank wait of the previous frame makes up to a refresh of lateness, which isn't one
			if (late > (this.vsync ? this.period / 2 : this.period / 4) || Volatile.Read(ref this.published) - entry.Frame >= Ring)
				continue;

			// The generator has until a quarter of a period after the due time; the CPU waits here, the game's queue never does
			GenerationTicket? ticket = this.TicketOf(entry);
			bool ready = false;
			if (ticket != null)
			{
				long budget = this.period / 4;
				long waitStart = Stopwatch.GetTimestamp();
				ready = ticket.Wait((uint)Math.Max(1, budget * 1000 / Stopwatch.Frequency));
				this.LearnGenerationLag(ready ? Stopwatch.GetTimestamp() - waitStart : budget);
			}

			// No lock is held while waiting: the main thread's Present re-shows the frame on screen meanwhile
			if (this.vsync)
				this.WaitForVBlank();

			lock (this.presentLock)
			{
				// The ring moves on while the generated frame and the vblank are waited for: a frame whose slots are reused by now
				// would show without its layers, the frame on screen stays instead
				if (Volatile.Read(ref this.published) - entry.Frame >= Ring)
					continue;

				if (ticket != null)
					this.FillGenerated(entry, ticket, ready);
				this.Show(this.Texture(entry.Texture), entry.Frame);
				this.swapChain->Present1(0, 0, &parameters);
				this.lastShownFrame = entry.Frame;
				this.lastShown = entry.Texture;
			}
		}
	}

	/// <summary>Blocks until the display's next vblank; an output that can't wait is given up on and frames go out unsynced</summary>
	private void WaitForVBlank()
	{
		IDXGIOutput* waiting = this.output;
		if (waiting != null && waiting->WaitForVBlank().FAILED)
		{
			Service.PluginLog.Warning("Waiting for the display's vblank failed, frames are presented unsynced");
			Dx.Release(waiting);
			this.output = null;
		}
	}

	/// <summary>
	/// The generated frame is due once the render thread's CPU work is done, but the generator also needs the frame's GPU work to be
	/// done and its own time: what the wait had to block for is added to later due times, and fades out while it isn't needed
	/// </summary>
	private void LearnGenerationLag(long blocked)
	{
		long lag = Volatile.Read(ref this.generationLag);
		lag = blocked > Millisecond / 10 ? lag + blocked : lag - (lag / 64);
		Volatile.Write(ref this.generationLag, Math.Min(lag, this.period / 2));
	}

	/// <summary>The generator's ticket of a generated frame's entry, null for a real frame or one that has its generated frame</summary>
	private GenerationTicket? TicketOf(Entry entry)
	{
		if ((entry.Texture & 1) != 0 || Volatile.Read(ref this.published) - entry.Frame >= Ring)
			return null;

		return this.records[(int)(entry.Frame % Ring)].Ticket;
	}

	/// <summary>Under the present lock: the generator's frame into the ring's texture, the real frame there instead when it wasn't ready</summary>
	private void FillGenerated(Entry entry, GenerationTicket ticket, bool ready)
	{
		int slot = (int)(entry.Frame % Ring);
		if (Volatile.Read(ref this.published) - entry.Frame >= Ring || this.records[slot].Ticket != ticket)
			return;

		this.records[slot].Ticket = null;
		if (ready && ticket.TryAcquire(this.context))
		{
			this.uiLayer.Blit(this.context, this.generatedView[slot], this.width, this.height, ticket.View);
			ticket.Release();
			return;
		}

		this.context->CopyResource((ID3D11Resource*)this.generated[slot], (ID3D11Resource*)this.real[slot]);
	}

	private void RegisterResizeCallbacks(Device* kernelDevice)
	{
		if (this.destroyCallback >= 0)
			return;

		this.destroyCallback = kernelDevice->OnResizeDestroy->AddCallback(
			(delegate* unmanaged<void*, bool>)(nint)(delegate* unmanaged<void*, byte>)&OnResizeDestroy, null);
		this.createCallback = kernelDevice->OnResizeCreate->AddCallback(
			(delegate* unmanaged<void*, bool>)(nint)(delegate* unmanaged<void*, byte>)&OnResizeCreate, null);
	}

	/// <summary>PostTick before the game releases the back buffer's contents: the original texture goes back</summary>
	[UnmanagedCallersOnly]
	private static byte OnResizeDestroy(void* context)
	{
		current?.Release();
		return 1;
	}

	/// <summary>The next Prepare starts again with the new back buffer</summary>
	[UnmanagedCallersOnly]
	private static byte OnResizeCreate(void* context) => 1;

	/// <summary>Main thread with the render thread idle (a Prepare, or the game's resize callbacks): thread stopped, texture handed back</summary>
	public void Release()
	{
		this.active = false;
		this.running = false;
		this.uiLayer.LayersAtPresent = false;
		Thread? stopping = this.thread;
		this.thread = null;
		stopping?.Join(2000);

		if (this.backBuffer != null && this.backBuffer->D3D11Texture2D == this.ours && this.ours != null)
		{
			this.backBuffer->D3D11Texture2D = this.originalTexture;
			this.backBuffer->MipRenderTargets[0].D3D11RenderTargetViewOrDepthStencilView = this.originalView;
		}

		this.backBuffer = null;
		for (int i = 0; i < Ring; i++)
		{
			Dx.Release(this.generatedView[i]);
			Dx.Release(this.generated[i]);
			Dx.Release(this.realView[i]);
			Dx.Release(this.real[i]);
			this.generatedView[i] = null;
			this.generated[i] = null;
			this.realView[i] = null;
			this.real[i] = null;
		}


		this.overlay.Disarm();
		for (int i = 0; i < OverlayRing; i++)
		{
			Dx.Release(this.layerResource[i]);
			Dx.Release(this.layerView[i]);
			Dx.Release(this.layer[i]);
			this.layerResource[i] = null;
			this.layerView[i] = null;
			this.layer[i] = null;
		}

		Dx.Release(this.composedResource);
		Dx.Release(this.composedView);
		Dx.Release(this.composed);
		Dx.Release(this.oursView);
		Dx.Release(this.ours);
		Dx.Release(this.bufferView);
		Dx.Release(this.buffer);
		Dx.Release(this.output);
		Dx.Release(this.swapChain);
		Dx.Release(this.context);
		Dx.Release(this.device);
		this.composedResource = null;
		this.composedView = null;
		this.composed = null;
		this.oursView = null;
		this.ours = null;
		this.bufferView = null;
		this.buffer = null;
		this.swapChain = null;
		this.output = null;
		this.context = null;
		this.device = null;
		this.lastShown = -1;
		this.overlayFinished = -1;
		lock (this.queueLock)
		{
			this.queueHead = 0;
			this.queueCount = 0;
		}
	}

	public void Dispose()
	{
		this.Release();
		this.overlay.Dispose();
		if (this.destroyCallback >= 0)
		{
			Device* kernelDevice = Device.Instance();
			if (kernelDevice != null)
			{
				kernelDevice->OnResizeDestroy->RemoveCallback(this.destroyCallback);
				kernelDevice->OnResizeCreate->RemoveCallback(this.createCallback);
			}

			this.destroyCallback = this.createCallback = -1;
		}

		if (current == this)
			current = null;
		GC.SuppressFinalize(this);
	}
}
