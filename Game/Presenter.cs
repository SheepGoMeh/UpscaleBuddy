using System;
using System.Runtime.InteropServices;
using System.Threading;

using FFXIVClientStructs.FFXIV.Client.Graphics.Kernel;

using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;

using static TerraFX.Interop.Windows.Windows;

using KernelTexture = FFXIVClientStructs.FFXIV.Client.Graphics.Kernel.Texture;

namespace UpscaleBuddy.Game;

/// <summary>
/// - While active, the back buffer's Kernel texture holds our texture (+0x68, +0x80[0]). The render thread writes the
///   next frame during this thread's Present.
/// - Exactly one game Present per rendered frame. An extra Present can fill the flip queue. A full flip queue blocks
///   Presents with sync interval 1 for about one refresh.
/// - Dalamud must not draw inside Present1. A Present1 on this thread would run Dalamud's UI on this thread. Dalamud's
///   ReShade handling modes other than Default draw inside ReShade's Present1. Usable is false until a main thread
///   probe (StartProbe, FinishProbe) shows that Dalamud does not draw there.
/// </summary>
public sealed unsafe class Presenter: IDisposable
{
	private const int DeviceRemoved = unchecked((int)0x887A0005);
	private const int DeviceReset = unchecked((int)0x887A0007);

	private delegate byte CallbackDelegate(void* context);

	private readonly UiLayer uiLayer;
	private readonly ReShadeEffects reShade;
	private readonly OverlayCapture overlay = new();
	private readonly LayerBlend blend = new();
	private readonly HighResolutionTimer timer = new();
	private readonly object presentLock = new();

	// Called by the game from PostTick
	private readonly CallbackDelegate destroyCallback;
	private int destroyCallbackIndex = -1;

	private readonly HANDLE work = CreateEventW(null, false, false, null);
	private readonly HANDLE stop = CreateEventW(null, true, false, null);
	private Thread? thread;
	private volatile bool running;
	private volatile bool failed;

	// The swap chain that dalamudInPresent1 applies to. Null means no probe result
	private IDXGISwapChain* probedSwapChain;
	private bool? dalamudInPresent1;

	private ID3D11DeviceContext* context;
	private IDXGISwapChain1* swapChain;
	private ID3D11Texture2D* buffer;
	private ID3D11RenderTargetView* bufferView;
	private uint width;
	private uint height;

	private KernelTexture* backBuffer;
	private void* originalTexture;
	private void* originalView;
	private ID3D11Texture2D* ours;
	private ID3D11RenderTargetView* oursView;

	private ID3D11Texture2D* real;

	// Dalamud's overlay, premultiplied alpha
	private ID3D11Texture2D* layer;
	private ID3D11RenderTargetView* layerView;
	private ID3D11ShaderResourceView* layerResource;
	private bool layerDrawn;

	// Protected by presentLock
	private long frame;
	private bool pending;
	private long realDue;
	private uint syncInterval;
	private bool effects;

	public Presenter(UiLayer uiLayer, ReShadeEffects reShade)
	{
		this.uiLayer = uiLayer;
		this.reShade = reShade;
		this.destroyCallback = this.OnResizeDestroy;
	}

	public bool Active => this.backBuffer != null;

	/// <summary>True when Dalamud draws inside Present1. Frame generation then presents on the game's thread</summary>
	public bool DalamudInPresent1 => this.dalamudInPresent1 == true;

	/// <summary>Exclusive fullscreen is not tested. A probe of this swap chain must show that Dalamud does not draw
	/// inside Present1</summary>
	public bool Usable(SwapChain* kernelSwapChain)
	{
		IDXGISwapChain* dxgi = (IDXGISwapChain*)kernelSwapChain->DXGISwapChain;
		BOOL fullscreen = false;
		return !this.failed && dxgi != null && dxgi == this.probedSwapChain && this.dalamudInPresent1 == false &&
		       dxgi->GetFullscreenState(&fullscreen, null).SUCCEEDED && !fullscreen;
	}

	/// <summary>True when this swap chain has no probe result</summary>
	public bool NeedsProbe(SwapChain* kernelSwapChain)
	{
		IDXGISwapChain* dxgi = (IDXGISwapChain*)kernelSwapChain->DXGISwapChain;
		return dxgi != null && (dxgi != this.probedSwapChain || this.dalamudInPresent1 == null);
	}

	/// <summary>
	/// Main thread, before the first of two game Presents. Keeps Dalamud's draw callback running while Dalamud hides
	/// plugin UI. Returns the draw count for FinishProbe
	/// </summary>
	public int StartProbe()
	{
		this.overlay.KeepDrawing(true);
		return this.overlay.Draws;
	}

	/// <summary>
	/// Main thread, in place of the second game Present. Presents with Present1. Records whether Dalamud drew inside
	/// it, only when Dalamud drew inside the first Present. Without a result, NeedsProbe stays true
	/// </summary>
	public void FinishProbe(SwapChain* kernelSwapChain, uint syncInterval, int drawsBeforeFirstPresent)
	{
		IDXGISwapChain* dxgi = (IDXGISwapChain*)kernelSwapChain->DXGISwapChain;
		int drawsBeforePresent1 = this.overlay.Draws;
		IDXGISwapChain1* swapChain1 = Dx.Query<IDXGISwapChain1>(dxgi);
		DXGI_PRESENT_PARAMETERS parameters = default;
		lock (this.reShade.PresentLock)
			swapChain1->Present1(syncInterval, 0, &parameters);
		swapChain1->Release();
		this.overlay.KeepDrawing(false);

		if (drawsBeforePresent1 == drawsBeforeFirstPresent)
			return;

		this.probedSwapChain = dxgi;
		this.dalamudInPresent1 = this.overlay.Draws != drawsBeforePresent1;
		if (this.dalamudInPresent1 == true)
			Service.PluginLog.Warning("Dalamud draws inside Present1. Frame generation presents on the game's thread. Dalamud's ReShade handling modes other than Default cause this.");
	}

	public void ResetFailure() => this.failed = false;

	/// <summary>The render thread must be idle</summary>
	/// <returns>False when the presenter cannot start</returns>
	public bool Present(Device* device, SwapChain* kernelSwapChain, long realAt, uint syncInterval, bool frameEffects, Action present)
	{
		if (!this.Active && !this.Start(device, kernelSwapChain))
			return false;

		lock (this.presentLock)
		{
			// Before CopyRegenerated, which overwrites ours
			this.context->CopyResource((ID3D11Resource*)this.real, (ID3D11Resource*)this.ours);
			bool generated = this.uiLayer.CopyRegenerated(kernelSwapChain);
			this.context->CopyResource((ID3D11Resource*)this.buffer, (ID3D11Resource*)(generated ? this.ours : this.real));
			this.PresentGenerated(frameEffects, present);

			this.frame++;
			this.pending = true;
			this.realDue = realAt;
			this.syncInterval = syncInterval;
			this.effects = frameEffects;
		}

		SetEvent(this.work);
		return true;
	}

	/// <summary>Requires presentLock</summary>
	private void PresentGenerated(bool frameEffects, Action present)
	{
		if (this.layerDrawn)
			this.blend.Draw(this.context, this.bufferView, this.width, this.height, this.layerResource, true);

		float* transparent = stackalloc float[4] { 0, 0, 0, 0 };
		this.context->ClearRenderTargetView(this.layerView, transparent);
		this.overlay.Arm(this.context, this.layerView);
		if (frameEffects)
			this.reShade.SkipNextPresent();
		present();
		this.overlay.Disarm();
		this.layerDrawn = true;
	}

	private void Run()
	{
		try
		{
			this.Loop();
		}
		catch (Exception e)
		{
			this.failed = true;
			this.running = false;
			Service.PluginLog.Error(e, "The frame generation presenter stopped, frames are presented on the game's thread");
		}
	}

	private void Loop()
	{
		HANDLE* handles = stackalloc HANDLE[2] { this.work, this.stop };
		while (this.running)
		{
			WaitForMultipleObjects(2, handles, false, INFINITE);
			while (this.running)
			{
				long current;
				long due;
				lock (this.presentLock)
				{
					if (!this.pending)
						break;

					current = this.frame;
					due = this.realDue;
				}

				if (!this.timer.Wait(due, this.stop))
					return;

				lock (this.presentLock)
				{
					if (!this.running || this.frame != current)
						continue;

					this.pending = false;
					this.ShowReal();
				}
			}
		}
	}

	/// <summary>Requires presentLock. Present1 must not hold the device lock (see ReShadeEffects.PresentLock)</summary>
	private void ShowReal()
	{
		DXGI_PRESENT_PARAMETERS parameters = default;
		this.context->CopyResource((ID3D11Resource*)this.buffer, (ID3D11Resource*)this.real);
		if (this.layerDrawn)
			this.blend.Draw(this.context, this.bufferView, this.width, this.height, this.layerResource, true);

		int result;
		lock (this.reShade.PresentLock)
		{
			if (this.effects)
				this.reShade.SkipNextPresent();

			result = this.swapChain->Present1(this.syncInterval, 0, &parameters);
		}

		if (result is DeviceRemoved or DeviceReset)
		{
			// The game's Present handles the device loss
			this.failed = true;
			this.running = false;
			Service.PluginLog.Warning($"Present1 failed (0x{result:X8}), frame generation presents on the game's thread");
		}
	}

	/// <summary>The render thread must be idle</summary>
	private bool Start(Device* device, SwapChain* kernelSwapChain)
	{
		KernelTexture* back = kernelSwapChain->BackBuffer;
		IDXGISwapChain* dxgi = (IDXGISwapChain*)kernelSwapChain->DXGISwapChain;
		if (back == null || dxgi == null || back->D3D11Texture2D == null || back->MipRenderTargets == null ||
		    back->MipRenderTargets[0].D3D11RenderTargetViewOrDepthStencilView == null)
			return false;

		try
		{
			this.swapChain = Dx.Query<IDXGISwapChain1>(dxgi);
			ID3D11Texture2D* swapChainBuffer;
			ThrowIfFailed(this.swapChain->GetBuffer(0, __uuidof<ID3D11Texture2D>(), (void**)&swapChainBuffer));
			this.buffer = swapChainBuffer;
			if (!Dx.IsSameObject(this.buffer, back->D3D11Texture2D))
				throw new InvalidOperationException("the back buffer's texture is not the swap chain's buffer");

			ID3D11Device* d3dDevice;
			this.buffer->GetDevice(&d3dDevice);
			try
			{
				ID3D11DeviceContext* immediate;
				d3dDevice->GetImmediateContext(&immediate);
				this.context = immediate;
				ID3D11Multithread* multithread = Dx.Query<ID3D11Multithread>(this.context);
				multithread->SetMultithreadProtected(true);
				multithread->Release();

				D3D11_TEXTURE2D_DESC desc = Dx.GetDesc(this.buffer);
				this.width = desc.Width;
				this.height = desc.Height;
				uint target = (uint)D3D11_BIND_FLAG.D3D11_BIND_RENDER_TARGET;
				uint targetAndResource = target | (uint)D3D11_BIND_FLAG.D3D11_BIND_SHADER_RESOURCE;
				this.ours = Dx.CreateTexture(d3dDevice, desc.Width, desc.Height, desc.Format, 1, target);
				this.oursView = CreateView(d3dDevice, this.ours);
				this.bufferView = CreateView(d3dDevice, this.buffer);
				this.real = Dx.CreateTexture(d3dDevice, desc.Width, desc.Height, desc.Format, 1, target);
				this.layer = Dx.CreateTexture(d3dDevice, desc.Width, desc.Height, desc.Format, 1, targetAndResource);
				this.layerView = CreateView(d3dDevice, this.layer);
				this.layerResource = Dx.CreateSrv(d3dDevice, this.layer);
			}
			finally
			{
				d3dDevice->Release();
			}

			this.context->CopyResource((ID3D11Resource*)this.ours, (ID3D11Resource*)this.buffer);
			this.originalTexture = back->D3D11Texture2D;
			this.originalView = back->MipRenderTargets[0].D3D11RenderTargetViewOrDepthStencilView;
			back->D3D11Texture2D = this.ours;
			back->MipRenderTargets[0].D3D11RenderTargetViewOrDepthStencilView = this.oursView;
			this.backBuffer = back;
		}
		catch (Exception e)
		{
			Service.PluginLog.Error(e, "The frame generation presenter did not start, frames are presented on the game's thread");
			this.Release();
			this.failed = true;
			return false;
		}

		if (this.destroyCallbackIndex < 0)
			this.destroyCallbackIndex = device->OnResizeDestroy->AddCallback(
				(delegate* unmanaged<void*, bool>)Marshal.GetFunctionPointerForDelegate(this.destroyCallback), null);

		this.layerDrawn = false;
		this.pending = false;
		this.overlay.KeepDrawing(true);
		ResetEvent(this.stop);
		this.running = true;
		this.thread = new Thread(this.Run) { Name = "UpscaleBuddy presenter", IsBackground = true, Priority = ThreadPriority.Highest };
		this.thread.Start();
		return true;
	}

	private static ID3D11RenderTargetView* CreateView(ID3D11Device* device, ID3D11Texture2D* texture)
	{
		ID3D11RenderTargetView* view;
		ThrowIfFailed(device->CreateRenderTargetView((ID3D11Resource*)texture, null, &view));
		return view;
	}

	/// <summary>The game releases the back buffer's D3D11 texture after this callback</summary>
	private byte OnResizeDestroy(void* callbackContext)
	{
		this.Release();
		return 1;
	}

	/// <summary>The render thread must be idle</summary>
	public void Release()
	{
		if (this.swapChain == null && this.thread == null)
			return;

		this.running = false;
		SetEvent(this.stop);
		this.thread?.Join();
		this.thread = null;
		this.overlay.Disarm();
		if (this.backBuffer != null)
		{
			this.overlay.KeepDrawing(false);
			if (this.backBuffer->D3D11Texture2D == this.ours)
			{
				this.context->CopyResource((ID3D11Resource*)this.buffer, (ID3D11Resource*)this.ours);
				this.backBuffer->D3D11Texture2D = this.originalTexture;
				this.backBuffer->MipRenderTargets[0].D3D11RenderTargetViewOrDepthStencilView = this.originalView;
			}

			this.backBuffer = null;
		}

		this.blend.Dispose();
		Dx.Release(this.layerResource);
		Dx.Release(this.layerView);
		Dx.Release(this.layer);
		Dx.Release(this.real);
		Dx.Release(this.oursView);
		Dx.Release(this.ours);
		Dx.Release(this.bufferView);
		Dx.Release(this.buffer);
		Dx.Release(this.swapChain);
		Dx.Release(this.context);
		this.layerResource = null;
		this.layerView = null;
		this.layer = null;
		this.real = null;
		this.oursView = null;
		this.ours = null;
		this.bufferView = null;
		this.buffer = null;
		this.swapChain = null;
		this.context = null;
	}

	/// <summary>The render thread must be idle</summary>
	public void Dispose()
	{
		this.Release();
		if (this.destroyCallbackIndex >= 0)
		{
			Device* device = Device.Instance();
			if (device != null)
				device->OnResizeDestroy->RemoveCallback(this.destroyCallbackIndex);
			this.destroyCallbackIndex = -1;
		}

		this.overlay.Dispose();
		this.timer.Dispose();
		CloseHandle(this.work);
		CloseHandle(this.stop);
	}
}
