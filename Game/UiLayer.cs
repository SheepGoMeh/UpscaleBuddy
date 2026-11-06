using System;
using System.Runtime.InteropServices;
using System.Threading;

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
/// Splits each frame into the scene and the UI. Frame generation needs these inputs. The class also finishes extra
/// frames the same way that the game finishes its own frames. It also splits the frame without frame generation when
/// ReShade's effects go under the UI.
/// - AtkServer.Draw (from RaptureAtkModule.Draw2D): binds RTM+0x570, the final target, and builds the UI's commands.
///   For this call, RTM+0x570 points to our layer. The detour restores it after the call.
/// - SetBlendState (render thread): while the layer is render target 0, the packed blend key gets alpha factors. These
///   factors accumulate coverage like the colour's destination term. Thus the layer is premultiplied alpha.
/// - Callbacks in view 31 around ToneAdjust (PostEffectManager.Submit, view 31, sort key 0xFFFFFFFF), after all UI:
///   - Before ToneAdjust, a callback copies the scene into a two frame ring and composites the layer onto the final
///     target. Thus ToneAdjust sees the scene and the UI as the game made them.
///   - After ToneAdjust, a callback builds a regenerated frame (scene + layer) and clears the layer.
/// - The regenerated frame goes through the game's own ToneAdjust chain (PostEffectManager+0x1B0) again. For that call,
///   the input and output of the chain point to our textures.
/// </summary>
public unsafe class UiLayer: IDisposable
{
	// RenderTargetManager: the Kernel::Texture* that the UI draws on. This is the back buffer, or ToneAdjustSource
	// (RTM+0x370) when ToneAdjust is on.
	private const int FinalTargetOffset = 0x570;

	// PostEffectManager+0x1B0: the ToneAdjust chain. Its input is PostEffectManager+0x4028 (= ToneAdjustSource). Its
	// output is +0x4020 (final).
	private const int ToneAdjustChainOffset = 0x1B0;
	private const int ChainInputOffset = 0x18; // PostEffectChain, Kernel::Texture*
	private const int PartOutputOffset = 0x38; // PostEffectPart, render target 0
	private const string SetChainInputSignature = "40 56 41 55 48 83 EC ?? 48 8B 41"; // sub_140349E00(chain, texture)
	private const string SetChainOutputSignature = "40 57 48 83 EC ?? 44 8B 41"; // sub_140349F00(chain, texture), last part
	private const string RenderChainSignature = "E8 ?? ?? ?? ?? 41 8B 46 ?? 8B DF"; // PostEffectChain.Render, from sub_140362540

	// With ToneAdjust off, Manager.Render (sub_140362D70) runs the ToneAdjust chain on the back buffer after the UI.
	// It runs the chain when PostEffectManager+0x4410 is -1 and one of these conditions is true:
	// - Device+0x98 (gamma) is not 1.
	// - Device+0x9C (color filter) is set and Device+0xA0 is more than 0.
	// The scene copies do not contain this pass.
	private const int ToneAdjustHandleOffset = 0x4410;
	private const int GammaOffset = 0x98;
	private const int ToneFlagOffset = 0x9C;
	private const int ToneAmountOffset = 0xA0;

	// RenderTargetManager.Initialize creates its targets with these values: TextureRenderTarget | ReadWrite |
	// UserManaged, allocation class 3.
	private const TextureFlags TargetFlags = TextureFlags.TextureRenderTarget | TextureFlags.ReadWrite | TextureFlags.UserManaged;
	private const uint AllocationClass = 3;

	// Callback command (ImmediateContextDX11.ExecuteCommands type 0xE): fn +0x8, arg +0x10, flags +0x18.
	// Our command keeps its arguments after it in the command buffer. They stay alive until the command executes.
	private const uint CallbackCommandType = 0xE;
	private const int CallbackSize = 0x20;
	private const int ToneAdjustView = 31;
	private const uint BeforeToneAdjust = 0x0FFFFFFE;
	// Same key as ToneAdjust. QueueFrame queues it later, so it sorts after ToneAdjust
	private const uint AfterToneAdjust = 0x0FFFFFFF;

	// Context: when this byte is 0, the game forces the top nibble of view 31 keys to 0xF
	private const int ContextSubViewFlagOffset = 0x2F70;

	private delegate void SetBlendStateDelegate(ImmediateContext* context, uint state);
	private delegate void SetTargetDelegate(ImmediateContext* context, nint command);

	// RenderCommandSetTarget: render targets 0-4 from +0x8, depth stencil +0x30 (CommandSetRenderTarget)
	private const int CommandTargetOffset = 0x8;
	private const int CommandDepthOffset = 0x30;
	private delegate void CallbackDelegate(nint* arguments);

	private readonly UpscaleBuddyConfiguration configuration;
	private readonly FrameGenerationRunner runner;
	private readonly ReShadeEffects reShade;
	private readonly Hook<AtkServer.Delegates.Draw>? drawHook;
	private readonly Hook<SetBlendStateDelegate>? setBlendStateHook;
	private readonly Hook<SetTargetDelegate>? setTargetHook;

	// The final target. The main thread sets it at the UI pass. Binds of this target with a depth buffer are
	// nameplates.
	private volatile nint finalTarget;
	private readonly delegate* unmanaged<PostEffectChain*, KernelTexture*, void> setChainInput;
	private readonly delegate* unmanaged<PostEffectChain*, KernelTexture*, void> setChainOutput;
	private readonly delegate* unmanaged<PostEffectChain*, void> renderChain;

	// These fields keep the delegates alive. The render thread calls them from the command buffer.
	private readonly CallbackDelegate compositeCallback;
	private readonly CallbackDelegate regenerateCallback;
	private readonly nint compositeFunction;
	private readonly nint regenerateFunction;

	// The main thread creates them. The render thread uses them through the callbacks' arguments.
	private volatile nint layer;
	private readonly KernelTexture*[] scenes = new KernelTexture*[2];
	private int sceneIndex;
	private KernelTexture* regeneratedSource;
	private KernelTexture* regeneratedTarget;
	private KernelTexture* regenerated; // Not owned. The target when ToneAdjust is on, the source when it is off
	private volatile bool stopped;

	// 1 when the regenerated textures hold the frame being presented. Regenerate sets it. The next Present clears it.
	private int ready;

	// Render thread
	private nint drawn;
	private bool composited;
	private KernelTexture* clearedLayer;
	private ID3D11DeviceContext1* context1;
	private ID3DDeviceContextState* state;
	private ID3D11VertexShader* vertexShader;
	private ID3D11PixelShader* pixelShader;
	private ID3D11BlendState* blendState;
	private ID3D11Texture2D* readable;
	private ID3D11ShaderResourceView* readableView;

	public UiLayer(UpscaleBuddyConfiguration configuration, FrameGenerationRunner runner, ReShadeEffects reShade)
	{
		this.configuration = configuration;
		this.runner = runner;
		this.reShade = reShade;
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
	/// Main thread, at Present. The render thread is idle. Copies the regenerated frame onto the back buffer. The
	/// regenerated frame is generated or repeated and has the UI. ToneAdjust finished it.
	/// </summary>
	/// <returns>False when the presented frame has no regenerated frame.</returns>
	public bool CopyRegenerated(SwapChain* swapChain)
	{
		if (Interlocked.Exchange(ref this.ready, 0) == 0 || this.regenerated == null || this.stopped)
			return false;

		this.Copy((ID3D11DeviceContext*)Device.Instance()->D3D11DeviceContext, swapChain->BackBuffer, this.regenerated);
		return true;
	}

	/// <summary>Main thread, at a Present that does not use the regenerated frame. Drops that frame.</summary>
	public void DiscardRegenerated() => Interlocked.Exchange(ref this.ready, 0);

	private KernelTexture* Layer => (KernelTexture*)this.layer;

	/// <summary>Main thread. AtkServer.Draw builds the UI's commands.</summary>
	private void DrawDetour(AtkServer* atkServer, bool flag)
	{
		KernelTexture** final = (KernelTexture**)((byte*)RenderTargetManager.Instance() + FinalTargetOffset);
		KernelTexture* target = *final;
		KernelTexture* ui = null;
		bool generate = this.runner.Enabled;
		bool effects = this.reShade.Active;
		if ((generate || effects) && !this.stopped && target != null)
		{
			KernelTexture* current = this.Layer;
			ui = this.Fit(ref current, target->ActualWidth, target->ActualHeight);
			this.layer = (nint)current;
		}

		if (ui == null || !generate)
		{
			this.runner.Skip();
			this.regenerated = null;
		}

		if (ui == null)
		{
			this.finalTarget = 0;
			this.drawHook!.Original(atkServer, flag);
			return;
		}

		this.finalTarget = (nint)target;
		*final = ui;
		this.drawHook!.Original(atkServer, flag);
		*final = target;

		this.QueueFrame(target, ui, generate, effects);
	}

	/// <summary>
	/// Returns a B8G8R8A8 render target of this size in the slot. Recreates it when the size changes. Returns null when
	/// creation fails. All our textures use one format. CreateTexture2D cannot create the back buffer's Kernel format
	/// (B8G8R8X8), so copies to and from the back buffer are blits.
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

	/// <summary>
	/// Main thread. Queues the frame's callbacks in view 31 around ToneAdjust. With frame generation, it also queues
	/// ToneAdjust for the regenerated frame. When effects is true, the composite callback applies ReShade's effects.
	/// </summary>
	private void QueueFrame(KernelTexture* target, KernelTexture* ui, bool generate, bool effects)
	{
		KernelTexture* scene = null;
		if (generate)
		{
			this.sceneIndex ^= 1;
			scene = this.Fit(ref this.scenes[this.sceneIndex], target->ActualWidth, target->ActualHeight);
		}

		PostEffectManager* postEffects = PostEffectManager.Instance();
		bool toneAdjust = (postEffects->Flags & PostEffectFlags.ToneAdjust) != 0 || InPlace(postEffects);
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

		// The composite callback does not use SetDefaultState. SetDefaultState records the back buffer as bound but does
		// not bind it, so ToneAdjust then skips its bind of the back buffer. ReShade restores the state that it changes
		context->SortKey = top | BeforeToneAdjust;
		this.QueueCallback(context, this.compositeFunction, false, (nint)target, (nint)ui, (nint)scene, effects ? 1 : 0);

		// Frame generation libraries change state freely. The game resets its state cache around the callback, like
		// for DLSS.
		context->SortKey = top | AfterToneAdjust;
		this.QueueCallback(context, this.regenerateFunction, generate, (nint)ui, (nint)scene, (nint)source, 0);
		if (source != null && toneAdjust && finished != null)
			this.QueueToneAdjust(postEffects, source, finished);

		context->SortKey = sortKey;
		context->ViewIndex = view;
	}

	/// <summary>
	/// When defaultState is true, ImmediateContextDX11.SetDefaultState runs before and after the callback. Use it for
	/// callbacks that change state directly.
	/// </summary>
	private void QueueCallback(Context* context, nint function, bool defaultState, nint first, nint second, nint third, nint fourth)
	{
		byte* command = (byte*)context->AllocateCommand((ulong)(CallbackSize + 4 * sizeof(nint)));
		if (command == null)
			return;

		nint* arguments = (nint*)(command + CallbackSize);
		arguments[0] = first;
		arguments[1] = second;
		arguments[2] = third;
		arguments[3] = fourth;
		*(uint*)command = CallbackCommandType;
		*(nint*)(command + 0x8) = function;
		*(nint*)(command + 0x10) = (nint)arguments;
		command[0x18] = defaultState ? (byte)1 : (byte)0;
		context->PushBackCommand(command);
	}

	/// <summary>
	/// Queues the game's ToneAdjust chain once more, from our source into our target. Restores the chain's own input
	/// and output after.
	/// </summary>
	private void QueueToneAdjust(PostEffectManager* postEffects, KernelTexture* source, KernelTexture* target)
	{
		PostEffectChain* chain = (PostEffectChain*)((byte*)postEffects + ToneAdjustChainOffset);
		KernelTexture* input = *(KernelTexture**)((byte*)chain + ChainInputOffset);
		byte* last = null;
		for (int i = (int)chain->PartCount - 1; i >= 0 && last == null; i--)
			last = (byte*)chain->Parts[i];
		if (last == null)
			return;

		// cToneMapParam contains the values for this frame. Manager.Render or Submit sets them before the UI pass
		KernelTexture* output = *(KernelTexture**)(last + PartOutputOffset);
		chain->Scale = 1;
		this.setChainInput(chain, source);
		this.setChainOutput(chain, target);
		this.renderChain(chain);
		this.setChainInput(chain, input);
		this.setChainOutput(chain, output);
	}

	/// <summary>Returns true when Manager.Render runs the ToneAdjust chain on the back buffer (the same test)</summary>
	private static bool InPlace(PostEffectManager* postEffects)
	{
		byte* device = (byte*)Device.Instance();
		if (device == null || *(long*)((byte*)postEffects + ToneAdjustHandleOffset) != -1)
			return false;

		float gamma = *(float*)(device + GammaOffset);
		return gamma != 1 || (*(int*)(device + ToneFlagOffset) != 0 && *(float*)(device + ToneAmountOffset) > 0);
	}

	/// <summary>
	/// Render thread. Nameplates draw into the final target with the display depth bound (view 30, sort key 0xCE...,
	/// before the 2D UI). The game depth tests them against the scene. This detour changes render target 0 of that bind
	/// to the layer. The depth stays bound. Thus the scene occludes the nameplates as before, and they draw into the
	/// layer like the rest of the UI.
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
		// Render target 0 (+0x28). CommandSetRenderTarget sets it.
		// Dalamud's ClientStructs has no CurrentRenderTargets yet.
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
	/// Makes every blend that the UI uses give layer = colour + layer * (1 - coverage). Thus the layer composites as
	/// premultiplied alpha. Coverage follows the colour's destination term:
	/// - Additive: coverage is kept.
	/// - Opaque: coverage is replaced.
	/// - Other blends: (1 - src alpha).
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

	/// <summary>
	/// Render thread, between the UI and ToneAdjust. Applies ReShade's effects to the scene, saves the scene and draws
	/// the layer on the final target
	/// </summary>
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
		if (arguments[3] != 0)
			this.reShade.Render(target);
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
	/// Render thread, after ToneAdjust. Draws the layer over the generated frame (or this frame's scene again) for the
	/// regenerated frame's ToneAdjust. Clears the layer.
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
			Interlocked.Exchange(ref this.ready, 1);
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

	/// <summary>
	/// Uses CopyResource when the D3D11 formats are the same, and a blit when they are different. The back buffer is
	/// R8G8B8A8 and our textures are B8G8R8A8. For a source without a shader view (the back buffer), the blit reads a
	/// copy in its own format
	/// </summary>
	private void Copy(ID3D11DeviceContext* context, KernelTexture* target, KernelTexture* source)
	{
		ID3D11Texture2D* to = (ID3D11Texture2D*)target->D3D11Texture2D;
		ID3D11Texture2D* from = (ID3D11Texture2D*)source->D3D11Texture2D;
		if (Dx.GetDesc(to).Format == Dx.GetDesc(from).Format)
		{
			context->CopyResource((ID3D11Resource*)to, (ID3D11Resource*)from);
			return;
		}

		ID3D11ShaderResourceView* view = (ID3D11ShaderResourceView*)source->D3D11ShaderResourceView;
		this.Draw(context, target, view != null ? view : this.ReadableCopy(context, from), false);
	}

	/// <summary>
	/// Copies the texture into a texture with a shader view and returns that view. Recreates the copy when the size or
	/// format changes
	/// </summary>
	private ID3D11ShaderResourceView* ReadableCopy(ID3D11DeviceContext* context, ID3D11Texture2D* texture)
	{
		D3D11_TEXTURE2D_DESC desc = Dx.GetDesc(texture);
		D3D11_TEXTURE2D_DESC current = this.readable != null ? Dx.GetDesc(this.readable) : default;
		if (current.Width != desc.Width || current.Height != desc.Height || current.Format != desc.Format)
		{
			Dx.Release(this.readableView);
			Dx.Release(this.readable);
			this.readableView = null;
			this.readable = null;

			ID3D11Device* device;
			context->GetDevice(&device);
			try
			{
				this.readable = Dx.CreateTexture(device, desc.Width, desc.Height, desc.Format, 1, (uint)D3D11_BIND_FLAG.D3D11_BIND_SHADER_RESOURCE);
				this.readableView = Dx.CreateSrv(device, this.readable);
			}
			finally
			{
				device->Release();
			}
		}

		context->CopyResource((ID3D11Resource*)this.readable, (ID3D11Resource*)texture);
		return this.readableView;
	}

	/// <summary>
	/// Draws the source over the target with a premultiplied alpha blend, or copies it. The draw uses its own context
	/// state, so the game's bindings and its state cache stay as they were.
	/// </summary>
	private void Draw(ID3D11DeviceContext* context, KernelTexture* target, KernelTexture* ui, bool blend) =>
		this.Draw(context, target, (ID3D11ShaderResourceView*)ui->D3D11ShaderResourceView, blend);

	private void Draw(ID3D11DeviceContext* context, KernelTexture* target, ID3D11ShaderResourceView* layerView, bool blend)
	{
		if (this.state == null)
			this.CreateResources(context);

		ID3DDeviceContextState* previous;
		this.context1->SwapDeviceContextState(this.state, &previous);

		ID3D11RenderTargetView* view = RenderTarget(target);
		D3D11_VIEWPORT viewport = new() { Width = target->ActualWidth, Height = target->ActualHeight, MaxDepth = 1 };
		context->OMSetRenderTargets(1, &view, null);
		context->OMSetBlendState(blend ? this.blendState : null, null, uint.MaxValue);
		context->RSSetViewports(1, &viewport);
		context->IASetPrimitiveTopology(D3D_PRIMITIVE_TOPOLOGY.D3D_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
		context->VSSetShader(this.vertexShader, null, 0);
		context->PSSetShader(this.pixelShader, null, 0);
		context->PSSetShaderResources(0, 1, &layerView);
		context->Draw(3, 0);

		// An inactive state that holds bindings would keep references to the targets, also through ResizeBuffers
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
			// The state must match a single threaded device
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

	/// <summary>
	/// Framework thread. Stops new callbacks, and queued callbacks do nothing. After this call, wait for the render
	/// thread before Dispose.
	/// </summary>
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

		Dx.Release(this.readableView);
		Dx.Release(this.readable);
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
