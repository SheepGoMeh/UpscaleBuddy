using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

using FFXIVClientStructs.FFXIV.Client.Graphics.Kernel;

using TerraFX.Interop.DirectX;

using static TerraFX.Interop.Windows.Windows;

using KernelTexture = FFXIVClientStructs.FFXIV.Client.Graphics.Kernel.Texture;

namespace UpscaleBuddy.Game;

/// <summary>
/// Frame generation's presents on a thread of their own, so the game's frame work isn't squeezed into half a rendered frame
/// around two Present calls. Needs the render thread idle at the handoff, which PostTick gives between "render done" and the
/// kick of the next frame.
/// - The back buffer's Kernel texture gets our D3D11 texture and render target view in place (Texture+0x68, +0x80[0]): the game
///   draws its final output, clears and screenshots there, and the swap chain's buffer is only touched by the present thread.
/// - Handoff, main thread: the finished frame and the generated one are copied into a ring, two presents are queued for the
///   pacer's deadline: the generated frame, half a rendered frame later the real one.
/// - Present thread: CopyResource into the swap chain's buffer and the present, under ID3D11Multithread protection of the game's
///   immediate context; no pipeline state is touched, so the render thread's state stays as it was.
/// - Dalamud draws inside IDXGISwapChain::Present, which Present1 isn't: the main thread still makes one Present call per real
///   frame (away from the present thread's slots) with the buffer holding the frame on screen, so it shows nothing new. That
///   draw stays on the main thread, at this point: it sets pipeline state on the game's immediate context, which the render
///   thread is using unless it's idle, and plugins' Draw callbacks read game state like the ObjectTable that only the main
///   thread may.
/// - Its overlay goes into a transparent texture of ours (<see cref="OverlayCapture"/>) and is laid over the real frame and the
///   generated one, so every frame the present thread shows has it.
/// - The game's resize callbacks (Device.OnResizeDestroy / OnResizeCreate) hand the original texture back before it's released.
/// </summary>
internal sealed unsafe class PresentThread: IDisposable
{
	private const int Ring = 4;
	private const int QueueSize = 16;
	private static readonly long Millisecond = Stopwatch.Frequency / 1000;

	private struct Entry
	{
		public long Due;
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
	private ID3D11Texture2D* buffer;
	private KernelTexture* backBuffer;
	private void* originalTexture;
	private void* originalView;
	private ID3D11Texture2D* ours;
	private ID3D11RenderTargetView* oursView;
	private readonly ID3D11Texture2D*[] real = new ID3D11Texture2D*[Ring];
	private readonly ID3D11RenderTargetView*[] realView = new ID3D11RenderTargetView*[Ring];
	private readonly ID3D11Texture2D*[] generated = new ID3D11Texture2D*[Ring];
	private readonly ID3D11RenderTargetView*[] generatedView = new ID3D11RenderTargetView*[Ring];

	// Dalamud's overlay of the frame being handed over, premultiplied alpha, laid over both of its frames
	private readonly OverlayCapture overlay = new();
	private ID3D11Texture2D* layer;
	private ID3D11RenderTargetView* layerView;
	private ID3D11ShaderResourceView* layerResource;

	// The frame handed over, queued by Commit once the overlay is on it
	private int pendingSlot;
	private long pendingGeneratedDue;
	private long pendingRealDue;
	private uint width;
	private uint height;
	private int frame;
	private long lastDue;
	private long half;
	private readonly long[] lateness = new long[32];
	private int latenessIndex;
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
	/// Main thread at Present, render thread idle. The finished frame (in our texture) and the generated one (null: the real
	/// one again) are copied and their times worked out; <see cref="NoopPresent"/> then puts the overlay on them and
	/// <see cref="Commit"/> queues them. False when the thread can't run here and the game's own Present should do it
	/// </summary>
	/// <param name="deadline">When the frame was meant to be done</param>
	/// <param name="period">One rendered frame</param>
	public bool Handoff(Device* kernelDevice, SwapChain* kernelSwapChain, KernelTexture* generatedFrame, long deadline, long period, long now)
	{
		if (this.active && (kernelSwapChain->BackBuffer != this.backBuffer || kernelSwapChain->BackBuffer->ActualWidth != this.width
			|| kernelSwapChain->BackBuffer->ActualHeight != this.height))
			this.Release();

		if (!this.active && (this.failed || !this.Start(kernelDevice, kernelSwapChain)))
			return false;

		int slot = this.frame++ % Ring;
		this.context->CopyResource((ID3D11Resource*)this.real[slot], (ID3D11Resource*)this.ours);
		ID3D11ShaderResourceView* source = generatedFrame != null ? (ID3D11ShaderResourceView*)generatedFrame->D3D11ShaderResourceView : null;
		if (source != null)
			this.uiLayer.Blit(this.context, this.generatedView[slot], this.width, this.height, source);
		else
			this.context->CopyResource((ID3D11Resource*)this.generated[slot], (ID3D11Resource*)this.ours);

		// How late frames have been decides how far behind its deadline a frame is shown, so a frame that's a little late still
		// lands on the grid
		this.lateness[this.latenessIndex] = Math.Max(0, now - deadline);
		this.latenessIndex = (this.latenessIndex + 1) % this.lateness.Length;
		long worst = 0;
		foreach (long late in this.lateness)
			worst = Math.Max(worst, late);

		this.half = period / 2;
		long offset = Math.Min(this.half, Millisecond + worst);
		long generatedDue = Math.Max(deadline + offset, this.lastDue + (Millisecond * 2));
		this.pendingSlot = slot;
		this.pendingGeneratedDue = generatedDue;
		this.pendingRealDue = generatedDue + this.half;
		this.lastDue = this.pendingRealDue;
		return true;
	}

	/// <summary>Main thread, after <see cref="NoopPresent"/>: the frame's presents are due</summary>
	public void Commit()
	{
		lock (this.queueLock)
		{
			while (this.queueCount > QueueSize - 2)
			{
				this.queueHead = (this.queueHead + 1) % QueueSize;
				this.queueCount--;
			}

			this.Enqueue(new Entry { Due = this.pendingGeneratedDue, Texture = this.pendingSlot * 2 });
			this.Enqueue(new Entry { Due = this.pendingRealDue, Texture = (this.pendingSlot * 2) + 1 });
		}
	}

	private void Enqueue(Entry entry)
	{
		this.queue[(this.queueHead + this.queueCount) % QueueSize] = entry;
		this.queueCount++;
	}

	/// <summary>
	/// Main thread, after the handoff: the game's own Present, for Dalamud's sake. The buffer holds what's on screen first, and
	/// it's kept away from the present thread's slots. Dalamud's overlay goes into a transparent texture instead of the buffer,
	/// then over the handed over frame and its generated one: every frame the thread shows has it
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

		float* transparent = stackalloc float[4] { 0, 0, 0, 0 };
		this.context->ClearRenderTargetView(this.layerView, transparent);
		this.overlay.Arm(this.context, this.layerView);
		lock (this.presentLock)
		{
			int shown = this.lastShown >= 0 ? this.lastShown : ((this.frame + Ring - 1) % Ring * 2) + 1;
			this.context->CopyResource((ID3D11Resource*)this.buffer, (ID3D11Resource*)this.Texture(shown));
			long begin = Stopwatch.GetTimestamp();
			present();
			long cost = Stopwatch.GetTimestamp() - begin;
			this.noopCost = ((this.noopCost * 3) + cost) / 4;
		}

		this.overlay.Disarm();
		this.uiLayer.BlendOver(this.context, this.realView[this.pendingSlot], this.width, this.height, this.layerResource);
		this.uiLayer.BlendOver(this.context, this.generatedView[this.pendingSlot], this.width, this.height, this.layerResource);
	}

	private ID3D11Texture2D* Texture(int code) => (code & 1) != 0 ? this.real[code / 2] : this.generated[code / 2];

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
			uint target = (uint)D3D11_BIND_FLAG.D3D11_BIND_RENDER_TARGET;
			for (int i = 0; i < Ring; i++)
			{
				this.real[i] = Dx.CreateTexture(this.device, desc.Width, desc.Height, desc.Format, 1, target);
				ThrowIfFailed(this.CreateView(this.real[i], out this.realView[i]));
				this.generated[i] = Dx.CreateTexture(this.device, desc.Width, desc.Height, desc.Format, 1, target);
				ThrowIfFailed(this.CreateView(this.generated[i], out this.generatedView[i]));
			}

			this.layer = Dx.CreateTexture(this.device, desc.Width, desc.Height, desc.Format, 1, bind);
			ThrowIfFailed(this.CreateView(this.layer, out this.layerView));
			this.layerResource = Dx.CreateSrv(this.device, this.layer);

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
		this.frame = 0;
		this.lastDue = 0;
		this.lastShown = -1;
		this.queueHead = 0;
		this.queueCount = 0;
		Array.Clear(this.lateness);
		this.running = true;
		this.active = true;
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

			// Woke far too late (or the frame was): presenting right behind the previous one would only look worse
			if (late > this.half / 2)
				continue;

			lock (this.presentLock)
			{
				this.context->CopyResource((ID3D11Resource*)this.buffer, (ID3D11Resource*)this.Texture(entry.Texture));
				this.swapChain->Present1(0, 0, &parameters);
			}

			this.lastShown = entry.Texture;
		}
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

	/// <summary>The next handoff starts again with the new back buffer</summary>
	[UnmanagedCallersOnly]
	private static byte OnResizeCreate(void* context) => 1;

	/// <summary>Main thread with the render thread idle (a handoff, or the game's resize callbacks): thread stopped, texture handed back</summary>
	public void Release()
	{
		this.active = false;
		this.running = false;
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
			Dx.Release(this.realView[i]);
			Dx.Release(this.real[i]);
			Dx.Release(this.generatedView[i]);
			Dx.Release(this.generated[i]);
			this.realView[i] = null;
			this.real[i] = null;
			this.generatedView[i] = null;
			this.generated[i] = null;
		}

		this.overlay.Disarm();
		Dx.Release(this.layerResource);
		Dx.Release(this.layerView);
		Dx.Release(this.layer);
		this.layerResource = null;
		this.layerView = null;
		this.layer = null;

		Dx.Release(this.oursView);
		Dx.Release(this.ours);
		Dx.Release(this.buffer);
		Dx.Release(this.swapChain);
		Dx.Release(this.context);
		Dx.Release(this.device);
		this.oursView = null;
		this.ours = null;
		this.buffer = null;
		this.swapChain = null;
		this.context = null;
		this.device = null;
		this.lastShown = -1;
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
