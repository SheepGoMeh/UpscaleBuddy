using System;
using System.Runtime.InteropServices;

using Dalamud.Hooking;

using FFXIVClientStructs.FFXIV.Client.Graphics.Kernel;
using FFXIVClientStructs.FFXIV.Client.Graphics.PostEffect;
using FFXIVClientStructs.FFXIV.Client.Graphics.Render;
using FFXIVClientStructs.FFXIV.Component.GUI;
using FFXIVClientStructs.Interop;

using TerraFX.Interop.DirectX;

using static TerraFX.Interop.DirectX.D3D11_BLEND;
using static TerraFX.Interop.DirectX.D3D11_BLEND_OP;
using static TerraFX.Interop.Windows.Windows;

using KernelTexture = FFXIVClientStructs.FFXIV.Client.Graphics.Kernel.Texture;

namespace UpscaleBuddy.Game;

/// <summary>
/// Splits each frame into the scene and the UI, the inputs frame generation needs, and finishes extra frames the way the
/// game finishes its own.
/// - AtkServer.Draw (from RaptureAtkModule.Draw2D): binds RTM+0x570, the final target, and builds the UI's commands.
///   Here RTM+0x570 is our layer for the call and restored after.
/// - SetBlendState (render thread): while the layer is render target 0 the packed blend key gets alpha factors that
///   accumulate coverage like the colour's destination term, so the layer is premultiplied alpha.
/// - Callbacks in view 31 around ToneAdjust (PostEffectManager.Submit, view 31, sort key 0xFFFFFFFF), after all UI:
///   before it the scene is copied into a two frame ring and the layer composited onto the final target, so ToneAdjust
///   sees scene and UI like the game made them; after it a regenerated frame (scene + layer) is built and the layer cleared.
/// - The regenerated frame goes through the game's own ToneAdjust chain (PostEffectManager+0x1B0) again, its input and
///   output pointed at our textures for the call.
/// </summary>
public unsafe class UiLayer: IDisposable
{
	// RenderTargetManager, Kernel::Texture* the UI is drawn on: the back buffer, or ToneAdjustSource (RTM+0x4E8) with ToneAdjust on
	private const int FinalTargetOffset = 0x570;

	// PostEffectManager+0x1B0: ToneAdjust, input PostEffectManager+0x4028 (= ToneAdjustSource), output +0x4020 (final)
	private const int ToneAdjustChainOffset = 0x1B0;
	private const int ChainInputOffset = 0x18; // PostEffectChain, Kernel::Texture*
	private const int PartOutputOffset = 0x38; // PostEffectPart, render target 0
	private const string SetChainInputSignature = "40 56 41 55 48 83 EC 28 48 8B 41 18"; // sub_140349BB0(chain, texture)
	private const string SetChainOutputSignature = "40 57 48 83 EC 20 44 8B 41 10"; // sub_140349CB0(chain, texture), last part
	private const string RenderChainSignature = "48 89 5C 24 08 48 89 74 24 10 57 48 83 EC 20 65 48 8B 04 25 ?? ?? ?? ?? 48 8B F9";

	// RenderTargetManager.Initialize's targets: TextureRenderTarget | ReadWrite | UserManaged, allocation class 3
	private const TextureFlags TargetFlags = TextureFlags.TextureRenderTarget | TextureFlags.ReadWrite | TextureFlags.UserManaged;
	private const uint AllocationClass = 3;

	// Callback command (ImmediateContextDX11.ExecuteCommands type 0xE): fn +0x8, arg +0x10, flags +0x18; ours carries its
	// arguments after it, in the command buffer, alive until executed
	private const uint CallbackCommandType = 0xE;
	private const int CallbackSize = 0x20;
	private const int ToneAdjustView = 31;
	private const uint BeforeToneAdjust = 0x0FFFFFFE;
	private const uint AfterToneAdjust = 0x0FFFFFFF; // Same key as ToneAdjust, queued later so sorted after it
	private const int ContextSubViewFlagOffset = 0x2F70; // Context: when 0 the top nibble of view 31 keys is forced to 0xF

	private delegate void SetBlendStateDelegate(ImmediateContext* context, uint state);
	private delegate void SetTargetDelegate(ImmediateContext* context, nint command);

	// RenderCommandSetTarget: render targets 0-4 from +0x8, depth stencil +0x30 (CommandSetRenderTarget)
	private const int CommandTargetOffset = 0x8;
	private const int CommandDepthOffset = 0x30;
	private delegate void CallbackDelegate(nint* arguments);

	private readonly UpscaleBuddyConfiguration configuration;
	private readonly FrameGenerationRunner runner;
	private readonly Hook<AtkServer.Delegates.Draw>? drawHook;
	private readonly Hook<SetBlendStateDelegate>? setBlendStateHook;
	private readonly Hook<SetTargetDelegate>? setTargetHook;

	// Main thread sets it at the UI pass: the final target, whose binds with a depth buffer are nameplates
	private volatile nint finalTarget;
	private readonly delegate* unmanaged<PostEffectChain*, KernelTexture*, void> setChainInput;
	private readonly delegate* unmanaged<PostEffectChain*, KernelTexture*, void> setChainOutput;
	private readonly delegate* unmanaged<PostEffectChain*, void> renderChain;

	// Kept alive here, the render thread calls them from the command buffer
	private readonly CallbackDelegate compositeCallback;
	private readonly CallbackDelegate regenerateCallback;
	private readonly nint compositeFunction;
	private readonly nint regenerateFunction;

	// Main thread creates them, the render thread uses them through the callbacks' arguments
	private volatile nint layer;
	private readonly KernelTexture*[] scenes = new KernelTexture*[2];
	private int sceneIndex;
	private KernelTexture* regeneratedSource;
	private KernelTexture* regeneratedTarget;
	private KernelTexture* regenerated; // Not owned: the target with ToneAdjust on, the source without
	private volatile bool stopped;

	// Render thread
	private nint drawn;
	private bool composited;
	private KernelTexture* clearedLayer;
	private ID3D11DeviceContext1* context1;
	private ID3DDeviceContextState* state;
	private ID3D11VertexShader* vertexShader;
	private ID3D11PixelShader* pixelShader;
	private ID3D11BlendState* blendState;

	public UiLayer(UpscaleBuddyConfiguration configuration, FrameGenerationRunner runner)
	{
		this.configuration = configuration;
		this.runner = runner;
		this.compositeCallback = this.Composite;
		this.regenerateCallback = this.Regenerate;
		this.compositeFunction = Marshal.GetFunctionPointerForDelegate(this.compositeCallback);
		this.regenerateFunction = Marshal.GetFunctionPointerForDelegate(this.regenerateCallback);
		try
		{
			this.setChainInput = (delegate* unmanaged<PostEffectChain*, KernelTexture*, void>)Service.SigScanner.ScanText(SetChainInputSignature);
			this.setChainOutput = (delegate* unmanaged<PostEffectChain*, KernelTexture*, void>)Service.SigScanner.ScanText(SetChainOutputSignature);
			this.renderChain = (delegate* unmanaged<PostEffectChain*, void>)Service.SigScanner.ScanText(RenderChainSignature);
			this.drawHook = Service.GameInteropProvider.HookFromAddress<AtkServer.Delegates.Draw>(
				(nint)AtkServer.MemberFunctionPointers.Draw, this.DrawDetour);
			this.setBlendStateHook = Service.GameInteropProvider.HookFromAddress<SetBlendStateDelegate>(
				(nint)ImmediateContext.MemberFunctionPointers.SetBlendState, this.SetBlendStateDetour);
			this.setTargetHook = Service.GameInteropProvider.HookFromAddress<SetTargetDelegate>(
				(nint)ImmediateContext.MemberFunctionPointers.DoSetTargetCommand, this.SetTargetDetour);
			this.drawHook.Enable();
			this.setBlendStateHook.Enable();
			this.setTargetHook.Enable();
			this.Available = true;
		}
		catch (Exception e)
		{
			Service.PluginLog.Warning(e, "UiLayer unavailable");
		}
	}

	public bool Available { get; }

	/// <summary>
	/// Main thread at Present, the render thread is idle: the regenerated frame (generated or repeated, with the UI, finished by
	/// ToneAdjust) onto the back buffer; false when there's none
	/// </summary>
	public bool CopyRegenerated(SwapChain* swapChain)
	{
		if (this.regenerated == null || this.stopped)
			return false;

		this.Copy((ID3D11DeviceContext*)Device.Instance()->D3D11DeviceContext, swapChain->BackBuffer, this.regenerated);
		return true;
	}

	private KernelTexture* Layer => (KernelTexture*)this.layer;

	/// <summary>Main thread, the UI's commands are being built</summary>
	private void DrawDetour(AtkServer* atkServer, bool flag)
	{
		KernelTexture** final = (KernelTexture**)((byte*)RenderTargetManager.Instance() + FinalTargetOffset);
		KernelTexture* target = *final;
		KernelTexture* ui = null;
		if (this.runner.Enabled && !this.stopped && target != null)
		{
			KernelTexture* current = this.Layer;
			ui = this.Fit(ref current, target->ActualWidth, target->ActualHeight);
			this.layer = (nint)current;
		}

		if (ui == null)
		{
			this.regenerated = null;
			this.finalTarget = 0;
			this.drawHook!.Original(atkServer, flag);
			return;
		}

		this.finalTarget = (nint)target;
		*final = ui;
		this.drawHook!.Original(atkServer, flag);
		*final = target;

		this.QueueFrame(target, ui);
	}

	/// <summary>
	/// A B8G8R8A8 render target of this size in the slot, recreated when it changes; null when creation fails. One format for
	/// all of ours: the back buffer's Kernel format (B8G8R8X8) can't be created, copies to and from it are blits
	/// </summary>
	private KernelTexture* Fit(ref KernelTexture* slot, uint width, uint height)
	{
		if (slot != null && slot->ActualWidth == width && slot->ActualHeight == height)
			return slot;

		// The game releases it once the frames using it are done
		if (slot != null)
			slot->DecRef();

		slot = KernelTexture.CreateTexture2D((int)width, (int)height, 1, TextureFormat.B8G8R8A8_UNORM, TargetFlags, AllocationClass);
		if (slot == null && this.failedSize != (width, height))
		{
			this.failedSize = (width, height);
			Service.PluginLog.Error($"Render target {width}x{height} couldn't be created, the UI stays on the scene");
		}

		return slot;
	}

	private (uint, uint) failedSize;

	/// <summary>Main thread: the frame's callbacks in view 31 around ToneAdjust, and the regenerated frame's ToneAdjust</summary>
	private void QueueFrame(KernelTexture* target, KernelTexture* ui)
	{
		this.sceneIndex ^= 1;
		KernelTexture* scene = this.Fit(ref this.scenes[this.sceneIndex], target->ActualWidth, target->ActualHeight);

		PostEffectManager* postEffects = PostEffectManager.Instance();
		bool toneAdjust = (postEffects->Flags & PostEffectFlags.ToneAdjust) != 0;
		KernelTexture* backBuffer = Device.Instance()->SwapChain->BackBuffer;
		KernelTexture* source = null;
		KernelTexture* finished = null;
		if (scene != null)
		{
			source = this.Fit(ref this.regeneratedSource, target->ActualWidth, target->ActualHeight);
			finished = toneAdjust ? this.Fit(ref this.regeneratedTarget, backBuffer->ActualWidth, backBuffer->ActualHeight) : source;
		}

		this.regenerated = finished;

		Context* context = ThreadLocals.ThreadLocalInstance()->GraphicsKernelContext;
		uint sortKey = context->SortKey;
		int view = context->ViewIndex;
		uint top = (*((byte*)context + ContextSubViewFlagOffset) == 0 ? sortKey | 0xF0000000 : sortKey) & 0xF0000000;
		context->ViewIndex = ToneAdjustView;

		context->SortKey = top | BeforeToneAdjust;
		this.QueueCallback(context, this.compositeFunction, false, (nint)target, (nint)ui, (nint)scene);

		// Frame generation libraries change state freely: the game resets its state cache around it, like for DLSS
		context->SortKey = top | AfterToneAdjust;
		this.QueueCallback(context, this.regenerateFunction, true, (nint)ui, (nint)scene, (nint)source);
		if (source != null && toneAdjust && finished != null)
			this.QueueToneAdjust(postEffects, source, finished);

		context->SortKey = sortKey;
		context->ViewIndex = view;
	}

	/// <summary>defaultState: ImmediateContextDX11.SetDefaultState before and after, for callbacks that change state directly</summary>
	private void QueueCallback(Context* context, nint function, bool defaultState, nint first, nint second, nint third)
	{
		byte* command = (byte*)context->AllocateCommand((ulong)(CallbackSize + 3 * sizeof(nint)));
		if (command == null)
			return;

		nint* arguments = (nint*)(command + CallbackSize);
		arguments[0] = first;
		arguments[1] = second;
		arguments[2] = third;
		*(uint*)command = CallbackCommandType;
		*(nint*)(command + 0x8) = function;
		*(nint*)(command + 0x10) = (nint)arguments;
		command[0x18] = defaultState ? (byte)1 : (byte)0;
		context->PushBackCommand(command);
	}

	/// <summary>The game's ToneAdjust chain once more, from our source into our target; its own input and output restored after</summary>
	private void QueueToneAdjust(PostEffectManager* postEffects, KernelTexture* source, KernelTexture* target)
	{
		PostEffectChain* chain = (PostEffectChain*)((byte*)postEffects + ToneAdjustChainOffset);
		KernelTexture* input = *(KernelTexture**)((byte*)chain + ChainInputOffset);
		byte* last = null;
		for (int i = (int)chain->PartCount - 1; i >= 0 && last == null; i--)
			last = (byte*)chain->Parts[i];
		if (last == null)
			return;

		KernelTexture* output = *(KernelTexture**)(last + PartOutputOffset);
		this.setChainInput(chain, source);
		this.setChainOutput(chain, target);
		this.renderChain(chain);
		this.setChainInput(chain, input);
		this.setChainOutput(chain, output);
	}

	/// <summary>
	/// Render thread: nameplates draw into the final target with the display depth bound (view 30, sort key 0xCE..., before the
	/// 2D UI), depth tested against the scene. That bind gets the layer as render target 0 instead, the depth stays, so
	/// they're occluded as before and drawn into the layer like the rest of the UI
	/// </summary>
	private void SetTargetDetour(ImmediateContext* context, nint command)
	{
		nint final = this.finalTarget;
		nint ui = this.layer;
		KernelTexture** target = (KernelTexture**)(command + CommandTargetOffset);
		if (final == 0 || ui == 0 || (nint)target[0] != final || *(KernelTexture**)(command + CommandDepthOffset) == null)
		{
			this.setTargetHook!.Original(context, command);
			return;
		}

		target[0] = (KernelTexture*)ui;
		this.setTargetHook!.Original(context, command);
		target[0] = (KernelTexture*)final;
	}

	/// <summary>Render thread, every draw</summary>
	private void SetBlendStateDetour(ImmediateContext* context, uint state)
	{
		// Render target 0 (+0x28, set by CommandSetRenderTarget); Dalamud's ClientStructs has no CurrentRenderTargets yet
		nint ui = this.layer;
#pragma warning disable CS0618
		if (ui != 0 && (nint)context->BackBufferReference == ui)
#pragma warning restore CS0618
		{
			state = Premultiplied(state);
			this.drawn = ui;
		}

		this.setBlendStateHook!.Original(context, state);
	}

	/// <summary>
	/// Layer = colour + layer * (1 - coverage) for every blend the UI uses, so it composites as premultiplied alpha.
	/// Coverage follows the colour's destination term: kept for additive, replaced for opaque, (1 - src alpha) otherwise.
	/// </summary>
	internal static uint Premultiplied(uint key)
	{
		PackedBlendStateDesc desc = *(PackedBlendStateDesc*)&key;
		if (!desc.BlendEnable)
		{
			desc.BlendEnable = true;
			desc.BlendOp = (byte)D3D11_BLEND_OP_ADD;
			desc.SrcBlend = (byte)D3D11_BLEND_ONE;
			desc.DestBlend = (byte)D3D11_BLEND_ZERO;
		}

		bool additive = desc.DestBlend == (byte)D3D11_BLEND_ONE;
		bool replace = desc.DestBlend == (byte)D3D11_BLEND_ZERO;
		desc.BlendOpAlpha = (byte)D3D11_BLEND_OP_ADD;
		desc.SrcBlendAlpha = (byte)(additive ? D3D11_BLEND_ZERO : D3D11_BLEND_ONE);
		desc.DestBlendAlpha = (byte)(additive ? D3D11_BLEND_ONE : replace ? D3D11_BLEND_ZERO : D3D11_BLEND_INV_SRC_ALPHA);
		desc.RenderTargetWriteMask |= (byte)D3D11_COLOR_WRITE_ENABLE.D3D11_COLOR_WRITE_ENABLE_ALPHA;
		return *(uint*)&desc;
	}

	/// <summary>Render thread, after the UI and before ToneAdjust: scene saved, layer onto the final target</summary>
	private void Composite(nint* arguments)
	{
		this.composited = false;
		if (this.stopped)
			return;

		KernelTexture* target = (KernelTexture*)arguments[0];
		KernelTexture* ui = (KernelTexture*)arguments[1];
		KernelTexture* scene = (KernelTexture*)arguments[2];
		bool drawn = this.drawn == (nint)ui;
		this.drawn = 0;
		ID3D11DeviceContext* context = (ID3D11DeviceContext*)Device.Instance()->D3D11DeviceContext;
		if (scene != null)
			this.Copy(context, scene, target);

		// A new layer has undefined content until its first clear
		if (ui != this.clearedLayer)
		{
			Clear(context, ui);
			this.clearedLayer = ui;
			return;
		}

		if (!drawn)
			return;

		this.Draw(context, target, ui, true);
		this.composited = true;
	}

	/// <summary>
	/// Render thread, after ToneAdjust: the generated frame (or this frame's scene again) + layer, for the regenerated frame's
	/// ToneAdjust; layer cleared
	/// </summary>
	private void Regenerate(nint* arguments)
	{
		if (this.stopped)
			return;

		KernelTexture* ui = (KernelTexture*)arguments[0];
		KernelTexture* scene = (KernelTexture*)arguments[1];
		KernelTexture* source = (KernelTexture*)arguments[2];
		ID3D11DeviceContext* context = (ID3D11DeviceContext*)Device.Instance()->D3D11DeviceContext;
		if (source != null && scene != null)
		{
			if (!this.runner.Generate(context, scene, source))
				context->CopyResource((ID3D11Resource*)source->D3D11Texture2D, (ID3D11Resource*)scene->D3D11Texture2D);
			if (this.composited)
				this.Draw(context, source, ui, true);
		}

		if (this.composited)
			Clear(context, ui);
		this.composited = false;
	}

	private static void Clear(ID3D11DeviceContext* context, KernelTexture* texture)
	{
		float* transparent = stackalloc float[4] { 0, 0, 0, 0 };
		context->ClearRenderTargetView(RenderTarget(texture), transparent);
	}

	private static ID3D11RenderTargetView* RenderTarget(KernelTexture* texture) =>
		(ID3D11RenderTargetView*)texture->GetMipRenderTarget(0)->D3D11RenderTargetViewOrDepthStencilView;

	/// <summary>CopyResource when the D3D11 formats match, a blit when they don't (the back buffer is R8G8B8A8, ours B8G8R8A8)</summary>
	private void Copy(ID3D11DeviceContext* context, KernelTexture* target, KernelTexture* source)
	{
		ID3D11Texture2D* to = (ID3D11Texture2D*)target->D3D11Texture2D;
		ID3D11Texture2D* from = (ID3D11Texture2D*)source->D3D11Texture2D;
		if (Dx.GetDesc(to).Format == Dx.GetDesc(from).Format)
			context->CopyResource((ID3D11Resource*)to, (ID3D11Resource*)from);
		else
			this.Draw(context, target, source, false);
	}

	/// <summary>
	/// Source over the target, premultiplied alpha blended or copied, in a context state of its own: the game's bindings and
	/// its state cache stay as they were
	/// </summary>
	private void Draw(ID3D11DeviceContext* context, KernelTexture* target, KernelTexture* ui, bool blend)
	{
		if (this.state == null)
			this.CreateResources(context);

		ID3DDeviceContextState* previous;
		this.context1->SwapDeviceContextState(this.state, &previous);

		ID3D11RenderTargetView* view = RenderTarget(target);
		ID3D11ShaderResourceView* layerView = (ID3D11ShaderResourceView*)ui->D3D11ShaderResourceView;
		D3D11_VIEWPORT viewport = new() { Width = target->ActualWidth, Height = target->ActualHeight, MaxDepth = 1 };
		context->OMSetRenderTargets(1, &view, null);
		context->OMSetBlendState(blend ? this.blendState : null, null, uint.MaxValue);
		context->RSSetViewports(1, &viewport);
		context->IASetPrimitiveTopology(D3D_PRIMITIVE_TOPOLOGY.D3D_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
		context->VSSetShader(this.vertexShader, null, 0);
		context->PSSetShader(this.pixelShader, null, 0);
		context->PSSetShaderResources(0, 1, &layerView);
		context->Draw(3, 0);

		// Bindings held in an inactive state would keep the targets referenced, through ResizeBuffers too
		ID3D11ShaderResourceView* noView = null;
		context->PSSetShaderResources(0, 1, &noView);
		context->OMSetRenderTargets(0, null, null);

		this.context1->SwapDeviceContextState(previous, null);
		previous->Release();
	}

	private void CreateResources(ID3D11DeviceContext* context)
	{
		ID3D11Device* device;
		context->GetDevice(&device);
		try
		{
			this.context1 = Dx.Query<ID3D11DeviceContext1>(context);
			ID3D11Device1* device1 = Dx.Query<ID3D11Device1>(device);
			D3D_FEATURE_LEVEL level = device->GetFeatureLevel();
			ID3DDeviceContextState* created;
			// Has to match a single threaded device
			uint flags = (device->GetCreationFlags() & (uint)D3D11_CREATE_DEVICE_FLAG.D3D11_CREATE_DEVICE_SINGLETHREADED) != 0
				? (uint)D3D11_1_CREATE_DEVICE_CONTEXT_STATE_FLAG.D3D11_1_CREATE_DEVICE_CONTEXT_STATE_SINGLETHREADED
				: 0;
			int hr = device1->CreateDeviceContextState(flags, &level, 1, D3D11.D3D11_SDK_VERSION, __uuidof<ID3D11Device>(), null, &created);
			device1->Release();
			ThrowIfFailed(hr);

			this.vertexShader = Dx.CreateVertexShader(device, "Shaders.ui_composite_vs.cso");
			this.pixelShader = Dx.CreatePixelShader(device, "Shaders.ui_composite_ps.cso");

			D3D11_BLEND_DESC desc = default;
			desc.RenderTarget[0] = new D3D11_RENDER_TARGET_BLEND_DESC
			{
				BlendEnable = true, SrcBlend = D3D11_BLEND_ONE, DestBlend = D3D11_BLEND_INV_SRC_ALPHA, BlendOp = D3D11_BLEND_OP_ADD,
				SrcBlendAlpha = D3D11_BLEND_ONE, DestBlendAlpha = D3D11_BLEND_INV_SRC_ALPHA, BlendOpAlpha = D3D11_BLEND_OP_ADD,
				RenderTargetWriteMask = (byte)D3D11_COLOR_WRITE_ENABLE.D3D11_COLOR_WRITE_ENABLE_ALL,
			};
			ID3D11BlendState* blend;
			ThrowIfFailed(device->CreateBlendState(&desc, &blend));
			this.blendState = blend;
			this.state = created;
		}
		finally
		{
			device->Release();
		}
	}

	/// <summary>Framework thread: no new callbacks, queued ones do nothing; then wait for the render thread before Dispose</summary>
	public void Stop() => this.stopped = true;

	public void Dispose()
	{
		this.stopped = true;
		this.drawHook?.Dispose();
		this.setBlendStateHook?.Dispose();
		this.setTargetHook?.Dispose();

		KernelTexture* current = this.Layer;
		this.layer = 0;
		Release(ref current);
		Release(ref this.scenes[0]);
		Release(ref this.scenes[1]);
		this.regenerated = null;
		Release(ref this.regeneratedTarget);
		Release(ref this.regeneratedSource);

		Dx.Release(this.blendState);
		Dx.Release(this.pixelShader);
		Dx.Release(this.vertexShader);
		Dx.Release(this.state);
		Dx.Release(this.context1);
		GC.SuppressFinalize(this);
	}

	private static void Release(ref KernelTexture* texture)
	{
		if (texture != null)
			texture->DecRef();
		texture = null;
	}
}
