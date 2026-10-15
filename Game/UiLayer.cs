using System;
using System.Numerics;
using System.Runtime.InteropServices;

using Dalamud.Hooking;

using FFXIVClientStructs.FFXIV.Client.Graphics.Kernel;
using FFXIVClientStructs.FFXIV.Client.Graphics.PostEffect;
using FFXIVClientStructs.FFXIV.Client.Graphics.Render;
using FFXIVClientStructs.FFXIV.Component.GUI;
using FFXIVClientStructs.Interop;

using UpscaleBuddy.FrameGeneration;

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
/// - The nameplates (a depth tested bind of the final target before the 2D UI) go into a layer of their own, the rest of the UI
///   into the other: the game draws them in that order, so plates, then the UI over them, is what it made. These working layers
///   are the same textures every frame; at the end of a frame the render thread copies them into that frame's slot of a ring
///   each, so a frame's layers stay until the presenter is done with them.
/// - SetBlendState (render thread): while a layer is render target 0 the packed blend key gets alpha factors that
///   accumulate coverage like the colour's destination term, so the layers are premultiplied alpha.
/// - Callbacks in view 31 around ToneAdjust (PostEffectManager.Submit, view 31, sort key 0xFFFFFFFF), after all UI:
///   before it the scene is copied into a two frame ring and the layers composited onto the final target, as the game's own
///   output (screenshots, ToneAdjust's input); after it the regenerated frame is built from the scene.
/// - With the present thread (and ToneAdjust off or only a gamma), the frames the presenter gets are the scene alone
///   (<see cref="FrameOutput"/>, real and generated) with the frame's layers next to them: the UI is never part of a generated
///   frame, the presenter lays the layers, as they are, over whatever frame it shows. Otherwise the regenerated frame carries
///   the layers and goes through the game's own ToneAdjust chain (PostEffectManager+0x1B0) again, its input and output pointed
///   at our textures for the call.
/// </summary>
public unsafe class UiLayer: IDisposable
{
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

	// Callback command (RenderCommandType.Callback): fn +0x8, arg +0x10, flags +0x18; ours carries its arguments after it, in
	// the command buffer, alive until executed
	private const uint CallbackCommandType = (uint)RenderCommandType.Callback;
	private const int CallbackSize = 0x20;
	private const int ToneAdjustView = 31;
	private const uint BeforeToneAdjust = 0x0FFFFFFE;
	private const uint AfterToneAdjust = 0x0FFFFFFF; // Same key as ToneAdjust, queued later so sorted after it
	private const int ContextSubViewFlagOffset = 0x2F70; // Context: when 0 the top nibble of view 31 keys is forced to 0xF

	// One slot per published frame of the presenter's ring
	private const int LayerRing = 4;

	private delegate void CallbackDelegate(nint* arguments);

	/// <summary>What a frame's callbacks work on, in the command buffer next to the callback</summary>
	[StructLayout(LayoutKind.Sequential)]
	private struct FrameArguments
	{
		public KernelTexture* Target;
		public KernelTexture* Scene;
		public KernelTexture* Source;
		public KernelTexture* Ui;
		public KernelTexture* Plates;
		public KernelTexture* RingUi;
		public KernelTexture* RingPlates;
		public byte Layered;

		// ToneAdjust's exponent when the presenter applies it, 0 when there is none to apply
		public float Gamma;
	}

	/// <summary>
	/// What the render thread's last frame made for the presenter. Layered: the scene and the generated frame without UI, and the
	/// layers the frame drew into (null when it drew nothing); otherwise the generated frame has the UI on it and the real frame
	/// is the back buffer's texture
	/// </summary>
	internal struct FrameOutput
	{
		public bool Layered;
		public KernelTexture* Real;
		public KernelTexture* Generated;

		// Layered, when the generator works off the render thread: the generated frame once it's done (Generated is null then)
		public GenerationTicket? Ticket;
		public KernelTexture* Ui;
		public KernelTexture* Plates;
		public float Gamma;
	}

	private readonly UpscaleBuddyConfiguration configuration;
	private readonly FrameGenerationRunner runner;
	private readonly Hook<AtkServer.Delegates.Draw>? drawHook;
	private readonly Hook<ImmediateContext.Delegates.SetBlendState>? setBlendStateHook;
	private readonly Hook<ImmediateContext.Delegates.DoSetTargetCommand>? setTargetHook;

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

	// Main thread creates them, the render thread uses them through the callbacks' arguments. The working layers are what the
	// UI passes draw into, the same textures every frame (the render thread's hooks compare against them while the main thread
	// is already recording the next frame); the render thread copies them into the frame's slot of the rings at the end of the frame
	private KernelTexture* uiWork;
	private KernelTexture* plateWork;
	private readonly KernelTexture*[] uiRing = new KernelTexture*[LayerRing];
	private readonly KernelTexture*[] plateRing = new KernelTexture*[LayerRing];
	private int ringSlot;
	private volatile nint layer;
	private volatile nint plateLayer;
	private readonly KernelTexture*[] scenes = new KernelTexture*[2];
	private int sceneIndex;
	private KernelTexture* regeneratedSource;
	private KernelTexture* regeneratedTarget;
	private KernelTexture* regenerated; // Not owned: the target with ToneAdjust on, the source without
	private volatile bool stopped;

	// Render thread: which layers the frame drew into and which of them went onto the final target (the ones to lay over the
	// regenerated frame), and the working layers that have been cleared since they were created
	private bool uiDrawn;
	private bool plateDrawn;
	private bool uiComposited;
	private bool plateComposited;
	private KernelTexture* clearedUi;
	private KernelTexture* clearedPlates;
	private FrameOutput output;
	private bool hasOutput;
	private readonly object createLock = new();
	private ID3D11DeviceContext1* context1;
	private ID3D11Multithread* multithread;
	private ID3DDeviceContextState* state;
	private ID3D11VertexShader* vertexShader;
	private ID3D11PixelShader* pixelShader;
	private ID3D11PixelShader* toneShader;
	private ID3D11Buffer* toneConstants;
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
			this.setBlendStateHook = Service.GameInteropProvider.HookFromAddress<ImmediateContext.Delegates.SetBlendState>(
				(nint)ImmediateContext.MemberFunctionPointers.SetBlendState, this.SetBlendStateDetour);
			this.setTargetHook = Service.GameInteropProvider.HookFromAddress<ImmediateContext.Delegates.DoSetTargetCommand>(
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

	/// <summary>
	/// The presenter lays the frames' layers over what it shows (set by the presenter on the main thread), when ToneAdjust doesn't
	/// need them in the frame
	/// </summary>
	internal volatile bool LayersAtPresent;

	/// <summary>Render thread, once per frame after its commands: what the frame made for the presenter; null without one</summary>
	internal bool TakeOutput(out FrameOutput taken)
	{
		taken = this.output;
		bool has = this.hasOutput && !this.stopped;
		this.hasOutput = false;
		return has;
	}

	/// <summary>Main thread, the UI's commands are being built</summary>
	private void DrawDetour(AtkServer* atkServer, bool flag)
	{
		// The Kernel::Texture the UI is drawn on: the back buffer, or ToneAdjustSource with ToneAdjust on
		KernelTexture** final = &RenderTargetManager.Instance()->SwapChainBackBuffer;
		KernelTexture* target = *final;
		KernelTexture* ui = null;
		KernelTexture* plates = null;
		KernelTexture* ringUi = null;
		KernelTexture* ringPlates = null;
		if (this.runner.Enabled && !this.stopped && target != null)
		{
			// The frame's slot of the rings, for the render thread to copy the working layers into
			int slot = (this.ringSlot + 1) % LayerRing;
			uint width = target->ActualWidth, height = target->ActualHeight;
			ui = this.Fit(ref this.uiWork, width, height);
			plates = this.Fit(ref this.plateWork, width, height);
			ringUi = this.Fit(ref this.uiRing[slot], width, height);
			ringPlates = this.Fit(ref this.plateRing[slot], width, height);
			if (ui != null && plates != null && ringUi != null && ringPlates != null)
				this.ringSlot = slot;
		}

		if (ui == null || plates == null || ringUi == null || ringPlates == null)
		{
			this.regenerated = null;
			this.finalTarget = 0;
			this.drawHook!.Original(atkServer, flag);
			return;
		}

		this.layer = (nint)ui;
		this.plateLayer = (nint)plates;
		this.finalTarget = (nint)target;
		*final = ui;
		this.drawHook!.Original(atkServer, flag);
		*final = target;

		this.QueueFrame(target, ui, plates, ringUi, ringPlates);
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
	private void QueueFrame(KernelTexture* target, KernelTexture* ui, KernelTexture* plates, KernelTexture* ringUi, KernelTexture* ringPlates)
	{
		this.sceneIndex ^= 1;
		KernelTexture* scene = this.Fit(ref this.scenes[this.sceneIndex], target->ActualWidth, target->ActualHeight);

		PostEffectManager* postEffects = PostEffectManager.Instance();
		bool toneAdjust = (postEffects->Flags & PostEffectFlags.ToneAdjust) != 0;
		Device* device = Device.Instance();
		KernelTexture* backBuffer = device->SwapChain->BackBuffer;
		KernelTexture* source = scene != null ? this.Fit(ref this.regeneratedSource, target->ActualWidth, target->ActualHeight) : null;

		// The presenter can do ToneAdjust's gamma itself, not its colour filter
		bool layered = this.LayersAtPresent && source != null && (!toneAdjust || device->ColorFilter == 0);
		KernelTexture* finished = source;
		if (source != null && toneAdjust && !layered)
			finished = this.Fit(ref this.regeneratedTarget, backBuffer->ActualWidth, backBuffer->ActualHeight);
		this.regenerated = finished;

		FrameArguments arguments = new()
		{
			Target = target, Scene = scene, Source = source, Ui = ui, Plates = plates, RingUi = ringUi, RingPlates = ringPlates,
			Layered = layered ? (byte)1 : (byte)0, Gamma = layered && toneAdjust ? device->GammaCorrection : 0,
		};

		Context* context = ThreadLocals.ThreadLocalInstance()->GraphicsKernelContext;
		uint sortKey = context->SortKey;
		int view = context->ViewIndex;
		uint top = (*((byte*)context + ContextSubViewFlagOffset) == 0 ? sortKey | 0xF0000000 : sortKey) & 0xF0000000;
		context->ViewIndex = ToneAdjustView;

		context->SortKey = top | BeforeToneAdjust;
		this.QueueCallback(context, this.compositeFunction, false, arguments);

		// Frame generation libraries change state freely: the game resets its state cache around it, like for DLSS
		context->SortKey = top | AfterToneAdjust;
		this.QueueCallback(context, this.regenerateFunction, true, arguments);
		if (source != null && toneAdjust && !layered && finished != null)
			this.QueueToneAdjust(postEffects, source, finished);

		context->SortKey = sortKey;
		context->ViewIndex = view;
	}

	/// <summary>defaultState: ImmediateContextDX11.SetDefaultState before and after, for callbacks that change state directly</summary>
	private void QueueCallback(Context* context, nint function, bool defaultState, in FrameArguments frame)
	{
		byte* command = (byte*)context->AllocateCommand((ulong)(CallbackSize + sizeof(FrameArguments)));
		if (command == null)
			return;

		nint* arguments = (nint*)(command + CallbackSize);
		*(FrameArguments*)arguments = frame;
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
	/// they're occluded as before and drawn into the nameplate layer
	/// </summary>
	private void SetTargetDetour(ImmediateContext* context, RenderCommandSetTarget* command)
	{
		nint final = this.finalTarget;
		nint plates = this.plateLayer;
		if (final == 0 || plates == 0 || (nint)command->RenderTargets[0].Value != final || command->DepthBuffer == null)
		{
			this.setTargetHook!.Original(context, command);
			return;
		}

		command->RenderTargets[0] = (KernelTexture*)plates;
		this.setTargetHook!.Original(context, command);
		command->RenderTargets[0] = (KernelTexture*)final;
	}

	/// <summary>Render thread, every draw</summary>
	private void SetBlendStateDetour(ImmediateContext* context, PackedBlendStateDesc state)
	{
		// Render target 0 (BackBufferReference, obsolete for CurrentRenderTargets)
#pragma warning disable CS0618
		nint target = (nint)context->BackBufferReference;
#pragma warning restore CS0618
		if (target != 0 && target == this.layer)
		{
			state = Premultiplied(state);
			this.uiDrawn = true;
		}
		else if (target != 0 && target == this.plateLayer)
		{
			state = Premultiplied(state);
			this.plateDrawn = true;
		}

		this.setBlendStateHook!.Original(context, state);
	}

	/// <summary>
	/// Layer = colour + layer * (1 - coverage) for every blend the UI uses, so it composites as premultiplied alpha.
	/// Coverage follows the colour's destination term: kept for additive, replaced for opaque, (1 - src alpha) otherwise.
	/// </summary>
	internal static PackedBlendStateDesc Premultiplied(PackedBlendStateDesc desc)
	{
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
		return desc;
	}

	/// <summary>Render thread, after the UI and before ToneAdjust: scene saved, the nameplates and then the UI onto the final target</summary>
	private void Composite(nint* arguments)
	{
		this.uiComposited = false;
		this.plateComposited = false;
		if (this.stopped)
			return;

		FrameArguments* frame = (FrameArguments*)arguments;
		KernelTexture* target = frame->Target;
		KernelTexture* ui = frame->Ui;
		KernelTexture* plates = frame->Plates;
		bool uiDrawn = this.uiDrawn;
		bool plateDrawn = this.plateDrawn;
		this.uiDrawn = false;
		this.plateDrawn = false;
		ID3D11DeviceContext* context = (ID3D11DeviceContext*)Device.Instance()->D3D11DeviceContext;
		if (frame->Scene != null)
			this.Copy(context, frame->Scene, target);

		// A new layer has undefined content until its first clear
		bool fresh = false;
		if (ui != this.clearedUi)
		{
			Clear(context, ui);
			this.clearedUi = ui;
			fresh = true;
		}

		if (plates != this.clearedPlates)
		{
			Clear(context, plates);
			this.clearedPlates = plates;
			fresh = true;
		}

		if (fresh)
			return;

		if (plateDrawn)
			this.Draw(context, target, plates, true);
		if (uiDrawn)
			this.Draw(context, target, ui, true);
		this.plateComposited = plateDrawn;
		this.uiComposited = uiDrawn;
	}

	/// <summary>
	/// Render thread, after ToneAdjust: the generated frame (or this frame's scene again). Layered, that is the output and the
	/// working layers are copied into the frame's slot of the rings; otherwise the layers go over it for the regenerated frame's
	/// ToneAdjust. The working layers are cleared for the next frame's UI
	/// </summary>
	private void Regenerate(nint* arguments)
	{
		if (this.stopped)
			return;

		FrameArguments* frame = (FrameArguments*)arguments;
		KernelTexture* source = frame->Source;
		KernelTexture* scene = frame->Scene;
		bool layered = frame->Layered != 0;
		ID3D11DeviceContext* context = (ID3D11DeviceContext*)Device.Instance()->D3D11DeviceContext;
		if (source != null && scene != null)
		{
			GenerationTicket? ticket = null;
			bool generated = layered ? (ticket = this.runner.Submit(context, scene)) != null : this.runner.Generate(context, scene, source);
			if (!generated)
				context->CopyResource((ID3D11Resource*)source->D3D11Texture2D, (ID3D11Resource*)scene->D3D11Texture2D);

			if (layered)
			{
				// The frame's layers into its slot of the rings, where they stay until the presenter is done with them
				if (this.uiComposited)
					context->CopyResource((ID3D11Resource*)frame->RingUi->D3D11Texture2D, (ID3D11Resource*)frame->Ui->D3D11Texture2D);
				if (this.plateComposited)
					context->CopyResource((ID3D11Resource*)frame->RingPlates->D3D11Texture2D, (ID3D11Resource*)frame->Plates->D3D11Texture2D);
				this.output = new FrameOutput
				{
					Layered = true, Real = scene, Generated = ticket == null ? source : null, Ticket = ticket,
					Ui = this.uiComposited ? frame->RingUi : null, Plates = this.plateComposited ? frame->RingPlates : null, Gamma = frame->Gamma,
				};
				this.hasOutput = true;
			}
			else
			{
				if (this.plateComposited)
					this.Draw(context, source, frame->Plates, true);
				if (this.uiComposited)
					this.Draw(context, source, frame->Ui, true);
			}
		}

		// Cleared whatever the frame drew into them: the next frame's UI passes start from nothing
		Clear(context, frame->Ui);
		Clear(context, frame->Plates);
		this.plateComposited = false;
		this.uiComposited = false;
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
	private void Draw(ID3D11DeviceContext* context, KernelTexture* target, KernelTexture* ui, bool blend) =>
		this.DrawView(context, RenderTarget(target), target->ActualWidth, target->ActualHeight, (ID3D11ShaderResourceView*)ui->D3D11ShaderResourceView, blend);

	/// <summary>The frame generation presenter's copy of a Kernel texture into a texture of its own (the format may differ)</summary>
	internal void Blit(ID3D11DeviceContext* context, ID3D11RenderTargetView* target, uint width, uint height, ID3D11ShaderResourceView* source) =>
		this.DrawView(context, target, width, height, source, false);

	/// <summary>The presenter's layer or overlay (premultiplied alpha) over a texture of its own</summary>
	internal void BlendOver(ID3D11DeviceContext* context, ID3D11RenderTargetView* target, uint width, uint height, ID3D11ShaderResourceView* overlay) =>
		this.DrawView(context, target, width, height, overlay, true);

	/// <summary>The regenerated frame while there is one, valid once the frame's commands are done</summary>
	internal KernelTexture* Regenerated => this.stopped ? null : this.regenerated;

	/// <summary>ToneAdjust's gamma over a texture of the presenter's into another (not the same one): the composed frame to the back buffer</summary>
	internal void Tone(ID3D11DeviceContext* context, ID3D11RenderTargetView* target, uint width, uint height, ID3D11ShaderResourceView* source, float gamma) =>
		this.DrawView(context, target, width, height, source, false, gamma);

	/// <summary>gamma above 0: the tone pass; otherwise a copy or a blend, pixel for pixel</summary>
	private void DrawView(ID3D11DeviceContext* context, ID3D11RenderTargetView* view, uint width, uint height, ID3D11ShaderResourceView* layerView, bool blend,
		float gamma = 0)
	{
		// The render thread and the present thread can both be first
		if (this.state == null)
		{
			lock (this.createLock)
			{
				if (this.state == null)
					this.CreateResources(context);
			}
		}

		// The swap out and back has to be one piece against the other threads sharing the context (the present thread's overlay
		// draw, the render thread's own calls): the context's lock is held across it
		this.multithread->Enter();
		try
		{
			ID3DDeviceContextState* previous;
			this.context1->SwapDeviceContextState(this.state, &previous);

			D3D11_VIEWPORT viewport = new() { Width = width, Height = height, MaxDepth = 1 };
			context->OMSetRenderTargets(1, &view, null);
			context->OMSetBlendState(blend ? this.blendState : null, null, uint.MaxValue);
			context->RSSetViewports(1, &viewport);
			context->IASetPrimitiveTopology(D3D_PRIMITIVE_TOPOLOGY.D3D_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
			context->VSSetShader(this.vertexShader, null, 0);
			if (gamma > 0)
			{
				Dx.Upload(context, this.toneConstants, new Vector4(gamma, 0, 0, 0));
				ID3D11Buffer* constants = this.toneConstants;
				context->PSSetShader(this.toneShader, null, 0);
				context->PSSetConstantBuffers(0, 1, &constants);
			}
			else
			{
				context->PSSetShader(this.pixelShader, null, 0);
			}

			context->PSSetShaderResources(0, 1, &layerView);
			context->Draw(3, 0);

			// Bindings held in an inactive state would keep the targets referenced, through ResizeBuffers too
			ID3D11ShaderResourceView* noView = null;
			context->PSSetShaderResources(0, 1, &noView);
			context->OMSetRenderTargets(0, null, null);

			this.context1->SwapDeviceContextState(previous, null);
			previous->Release();
		}
		finally
		{
			this.multithread->Leave();
		}
	}

	private void CreateResources(ID3D11DeviceContext* context)
	{
		ID3D11Device* device;
		context->GetDevice(&device);
		try
		{
			this.context1 = Dx.Query<ID3D11DeviceContext1>(context);
			this.multithread = Dx.Query<ID3D11Multithread>(context);
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
			this.toneShader = Dx.CreatePixelShader(device, "Shaders.tone_adjust_ps.cso");
			this.toneConstants = Dx.CreateConstantBuffer(device, (uint)sizeof(Vector4));

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

		this.layer = 0;
		this.plateLayer = 0;
		Release(ref this.uiWork);
		Release(ref this.plateWork);
		for (int i = 0; i < LayerRing; i++)
		{
			Release(ref this.uiRing[i]);
			Release(ref this.plateRing[i]);
		}

		Release(ref this.scenes[0]);
		Release(ref this.scenes[1]);
		this.regenerated = null;
		Release(ref this.regeneratedTarget);
		Release(ref this.regeneratedSource);

		Dx.Release(this.toneConstants);
		Dx.Release(this.toneShader);
		Dx.Release(this.blendState);
		Dx.Release(this.pixelShader);
		Dx.Release(this.vertexShader);
		Dx.Release(this.state);
		Dx.Release(this.multithread);
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
