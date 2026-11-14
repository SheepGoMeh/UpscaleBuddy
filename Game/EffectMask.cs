using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

using Dalamud.Hooking;

using FFXIVClientStructs.FFXIV.Client.Graphics.Kernel;
using FFXIVClientStructs.FFXIV.Client.Graphics.PostEffect;
using FFXIVClientStructs.FFXIV.Client.Graphics.Render;

using TerraFX.Interop.DirectX;

using static TerraFX.Interop.DirectX.DXGI_FORMAT;

using KernelTexture = FFXIVClientStructs.FFXIV.Client.Graphics.Kernel.Texture;

namespace UpscaleBuddy.Game;

/// <summary>
/// The reactive mask of the upscalers, from rain and VFX coverage. The game writes this coverage into a second render
/// target in the depth of field effect mask variants of WeatherRenderer and Apricot. That target is RTM+0x680, render
/// target 1 next to SceneInput. Normal play does not use those variants. They need PostEffectFlags.DepthOfField and
/// DepthOfField.UseUpdatedDepthOfField. EffectMask changes the game as follows:
/// - From WeatherRenderer.Render to its finish (sub_1402ABEA0, after its jobs are joined), both flags are set. So the
///   draws of the weather use the variant. In that span of Manager.RenderView, only the weather reads the flags.
///   PostEffectManager.Submit, the user of the depth of field, ran before.
/// - Manager.Render's copy of the condition for Apricot (sub_140393730, after all views) is true.
/// - In Context.SetRenderTargets, render target 1 of those binds is ours. So the game's depth of field parameters stay
///   untouched. Ours holds only the coverage of the effects, in alpha.
/// EffectMask does none of this while the game's own depth of field uses the mask.
/// Render thread, at the upscaler. Writes alpha x responsiveness into an R8 texture shared with D3D12. Then clears ours
/// for the next frame.
/// </summary>
public unsafe class EffectMask: IDisposable
{
	private const string WeatherRenderSignature = "40 57 48 83 EC ?? C6 81 ?? ?? ?? ?? ?? 48 8B F9";
	private const string WeatherFinishSignature = "E8 ?? ?? ?? ?? 48 8D BB ?? ?? ?? ?? 4C 39 6F"; // From Manager.RenderView
	private const string EffectsMaskEnabledSignature = "E8 ?? ?? ?? ?? E8 ?? ?? ?? ?? 4C 8B 35"; // From Manager.Render

	private const int GameMaskOffset = 0x680; // RenderTargetManager: DepthOfFieldEffectMask, B8G8R8A8, allocated at the display size

	private const TextureFlags TargetFlags = TextureFlags.TextureRenderTarget | TextureFlags.ReadWrite | TextureFlags.UserManaged;
	private const uint AllocationClass = 3;

	[StructLayout(LayoutKind.Sequential)]
	private struct Constants
	{
		public float Scale;
		public float Padding0, Padding1, Padding2;
	}

	// Ghidra types these as void. IDA's Render returns rax, so the hook passes it through. SetRenderTargets uses the
	// signature that ClientStructs documents, because Dalamud's hook verification requires it
	private delegate nint WeatherRenderDelegate(nint renderer);
	private delegate void WeatherFinishDelegate(nint renderer);
	private delegate void EffectsMaskEnabledDelegate(byte enabled);
	private delegate void SetRenderTargetsDelegate(Context* context, int count, KernelTexture** targets, KernelTexture* depth, short a4,
		short a5, short a6, short a7);

	private readonly Hook<WeatherRenderDelegate>? weatherRenderHook;
	private readonly Hook<WeatherFinishDelegate>? weatherFinishHook;
	private readonly Hook<EffectsMaskEnabledDelegate>? effectsMaskEnabledHook;
	private readonly Hook<SetRenderTargetsDelegate>? setRenderTargetsHook;

	// The framework thread writes these. The main thread and the render thread read them
	private volatile bool active;
	private volatile float responsiveness;

	// Main thread
	private readonly HashSet<nint> forcedWeather = [];
	private PostEffectFlags savedDepthOfField;
	private bool savedUpdatedDepthOfField;
	private bool loggedUnfinishedWeather;

	// The main thread creates the mask. SetRenderTargets (any thread) and the render thread use it
	private volatile nint mask;
	private volatile bool redirectWeather;
	private volatile bool redirectEffects;
	private volatile bool stopped;

	// Render thread
	private ID3D11Device* device;
	private ID3D11ComputeShader* shader;
	private ID3D11Buffer* constantBuffer;
	private ID3D11Texture2D* reactive;
	private ID3D11UnorderedAccessView* reactiveUav;
	private nint clearedMask;
	private bool reactiveWritten;

	public EffectMask()
	{
		try
		{
			this.weatherRenderHook = Service.GameInteropProvider.HookFromAddress<WeatherRenderDelegate>(
				Service.SigScanner.ScanText(WeatherRenderSignature), this.WeatherRenderDetour);
			this.weatherFinishHook = Service.GameInteropProvider.HookFromAddress<WeatherFinishDelegate>(
				Service.SigScanner.ScanText(WeatherFinishSignature), this.WeatherFinishDetour);
			this.effectsMaskEnabledHook = Service.GameInteropProvider.HookFromAddress<EffectsMaskEnabledDelegate>(
				Service.SigScanner.ScanText(EffectsMaskEnabledSignature), this.EffectsMaskEnabledDetour);
			this.setRenderTargetsHook = Service.GameInteropProvider.HookFromAddress<SetRenderTargetsDelegate>(
				(nint)Context.MemberFunctionPointers.SetRenderTargets, this.SetRenderTargetsDetour);
			this.weatherRenderHook.Enable();
			this.weatherFinishHook.Enable();
			this.effectsMaskEnabledHook.Enable();
			this.setRenderTargetsHook.Enable();
			this.Available = true;
		}
		catch (Exception e)
		{
			this.weatherRenderHook?.Dispose();
			this.weatherFinishHook?.Dispose();
			this.effectsMaskEnabledHook?.Dispose();
			this.setRenderTargetsHook?.Dispose();
			Service.PluginLog.Warning(e, "EffectMask unavailable, rain and effects get no reactive mask");
		}
	}

	public bool Available { get; }

	/// <summary>Framework thread. Activates EffectMask while an upscaler runs and responsiveness is above 0</summary>
	public void Configure(bool upscaling, float effectResponsiveness)
	{
		bool wasActive = this.active;
		this.responsiveness = effectResponsiveness;
		this.active = this.Available && !this.stopped && upscaling && effectResponsiveness > 0;
		if (this.active != wasActive)
			Service.PluginLog.Information($"EffectMask {(this.active ? "on" : "off")}, responsiveness {effectResponsiveness}");
	}

	private KernelTexture* Mask => (KernelTexture*)this.mask;

	private static KernelTexture* GameMask
	{
		get
		{
			byte* targets = (byte*)RenderTargetManager.Instance();
			return targets == null ? null : *(KernelTexture**)(targets + GameMaskOffset);
		}
	}

	private static bool GameUsesMask(PostEffectManager* postEffects) =>
		(postEffects->Flags & PostEffectFlags.DepthOfField) != 0 && postEffects->DepthOfField.UseUpdatedDepthOfField;

	/// <summary>Main thread, Manager.RenderView after PostEffectManager.Submit</summary>
	private nint WeatherRenderDetour(nint renderer)
	{
		PostEffectManager* postEffects = PostEffectManager.Instance();
		if (!this.active || postEffects == null || !this.FitMask())
			return this.weatherRenderHook!.Original(renderer);

		if (this.forcedWeather.Count == 0)
		{
			if (GameUsesMask(postEffects))
				return this.weatherRenderHook!.Original(renderer);

			this.savedDepthOfField = postEffects->Flags & PostEffectFlags.DepthOfField;
			this.savedUpdatedDepthOfField = postEffects->DepthOfField.UseUpdatedDepthOfField;
			postEffects->Flags |= PostEffectFlags.DepthOfField;
			postEffects->DepthOfField.UseUpdatedDepthOfField = true;
		}

		this.forcedWeather.Add(renderer);
		this.redirectWeather = true;
		nint result = this.weatherRenderHook!.Original(renderer);
		this.redirectWeather = false;
		return result;
	}

	/// <summary>Main thread, Manager.RenderView. Original joins the renderer's jobs</summary>
	private void WeatherFinishDetour(nint renderer)
	{
		this.weatherFinishHook!.Original(renderer);
		if (this.forcedWeather.Remove(renderer) && this.forcedWeather.Count == 0)
			this.RestoreDepthOfField();
	}

	private void RestoreDepthOfField()
	{
		PostEffectManager* postEffects = PostEffectManager.Instance();
		postEffects->Flags = (postEffects->Flags & ~PostEffectFlags.DepthOfField) | this.savedDepthOfField;
		postEffects->DepthOfField.UseUpdatedDepthOfField = this.savedUpdatedDepthOfField;
	}

	/// <summary>Main thread, Manager.Render after all views, before Apricot's draws are built</summary>
	private void EffectsMaskEnabledDetour(byte enabled)
	{
		// The finish of a weather renderer did not run. Restore the game's state before anything else reads it.
		// Also pass the game's own condition
		if (this.forcedWeather.Count > 0)
		{
			if (!this.loggedUnfinishedWeather)
			{
				this.loggedUnfinishedWeather = true;
				Service.PluginLog.Warning("A weather renderer rendered without its finish, depth of field flags restored late");
			}

			this.forcedWeather.Clear();
			this.RestoreDepthOfField();
			enabled = this.savedDepthOfField != 0 && this.savedUpdatedDepthOfField ? (byte)1 : (byte)0;
		}

		this.redirectEffects = this.active && enabled == 0 && this.FitMask();
		this.effectsMaskEnabledHook!.Original(this.redirectEffects ? (byte)1 : enabled);
	}

	/// <summary>Any thread that builds commands</summary>
	private void SetRenderTargetsDetour(Context* context, int count, KernelTexture** targets, KernelTexture* depth, short a4, short a5,
		short a6, short a7)
	{
		nint ours = this.mask;
		if (ours == 0 || count != 2 || !(this.redirectWeather || this.redirectEffects) || targets[1] != GameMask)
		{
			this.setRenderTargetsHook!.Original(context, count, targets, depth, a4, a5, a6, a7);
			return;
		}

		KernelTexture** redirected = stackalloc KernelTexture*[2] { targets[0], (KernelTexture*)ours };
		this.setRenderTargetsHook!.Original(context, count, redirected, depth, a4, a5, a6, a7);
	}

	/// <summary>
	/// Main thread. Returns false when the game has no mask.
	/// The plugin's mask has the allocated size of the game's targets. D3D11 binds only render targets of one size.
	/// </summary>
	private bool FitMask()
	{
		KernelTexture* game = GameMask;
		if (game == null)
			return false;

		KernelTexture* current = this.Mask;
		if (current != null && current->AllocatedWidth == game->AllocatedWidth && current->AllocatedHeight == game->AllocatedHeight)
			return true;

		// The game releases it when the frames that use it are done
		if (current != null)
			current->DecRef();
		current = KernelTexture.CreateTexture2D((int)game->AllocatedWidth, (int)game->AllocatedHeight, 1, TextureFormat.B8G8R8A8_UNORM,
			TargetFlags, AllocationClass);
		this.mask = (nint)current;
		if (current == null)
			Service.PluginLog.Warning($"EffectMask target {game->AllocatedWidth}x{game->AllocatedHeight} couldn't be created");
		return current != null;
	}

	/// <summary>
	/// Render thread, at the upscaler, after the frame's effects. Returns the reactive mask at the size of the color.
	/// The reactive mask is zero where nothing was drawn and while EffectMask is off. Clears ours for the next frame.
	/// Returns null when unavailable
	/// </summary>
	public ID3D11Texture2D* Resolve(ID3D11DeviceContext* context, ID3D11Texture2D* color)
	{
		if (!this.Available || this.stopped || color == null)
			return null;

		D3D11_TEXTURE2D_DESC desc = Dx.GetDesc(color);
		this.FitReactive(context, desc.Width, desc.Height);

		KernelTexture* mask = this.Mask;
		float* zero = stackalloc float[4] { 0, 0, 0, 0 };
		bool written = false;
		if (mask != null)
		{
			// A new texture has undefined content until its first clear
			if ((nint)mask == this.clearedMask && this.active)
			{
				this.Convert(context, mask, desc.Width, desc.Height);
				written = true;
			}

			ID3D11RenderTargetView* maskRtv = (ID3D11RenderTargetView*)mask->GetMipRenderTarget(0)->D3D11RenderTargetViewOrDepthStencilView;
			context->ClearRenderTargetView(maskRtv, zero);
			this.clearedMask = (nint)mask;
		}

		if (!written && this.reactiveWritten)
			context->ClearUnorderedAccessViewFloat(this.reactiveUav, zero);

		this.reactiveWritten = written;
		return this.reactive;
	}

	private void Convert(ID3D11DeviceContext* context, KernelTexture* mask, uint width, uint height)
	{
		Dx.Upload(context, this.constantBuffer, new Constants { Scale = this.responsiveness });
		ID3D11ShaderResourceView* srv = (ID3D11ShaderResourceView*)mask->D3D11ShaderResourceView;
		ID3D11UnorderedAccessView* uav = this.reactiveUav;
		ID3D11Buffer* buffer = this.constantBuffer;
		ID3D11ShaderResourceView* noSrv = null;
		ID3D11UnorderedAccessView* noUav = null;

		context->CSSetShaderResources(0, 1, &srv);
		context->CSSetUnorderedAccessViews(0, 1, &uav, null);
		context->CSSetConstantBuffers(0, 1, &buffer);
		context->CSSetShader(this.shader, null, 0);
		context->Dispatch((width + 7) / 8, (height + 7) / 8, 1);

		context->CSSetUnorderedAccessViews(0, 1, &noUav, null);
		context->CSSetShaderResources(0, 1, &noSrv);
		context->CSSetShader(null, null, 0);
	}

	/// <summary>
	/// Fits the R8 reactive texture to this size and recreates it when the size changes. The texture is NT shared, so
	/// the D3D12 upscalers open it directly. The upscalers keep their own references to the old texture as long as they
	/// cache it
	/// </summary>
	private void FitReactive(ID3D11DeviceContext* context, uint width, uint height)
	{
		if (this.device == null)
		{
			ID3D11Device* created;
			context->GetDevice(&created);
			this.device = created;
			this.shader = Dx.CreateComputeShader(created, "Shaders.effect_reactive.cso");
			this.constantBuffer = Dx.CreateConstantBuffer(created, (uint)sizeof(Constants));
		}

		if (this.reactive != null)
		{
			D3D11_TEXTURE2D_DESC desc = Dx.GetDesc(this.reactive);
			if (desc.Width == width && desc.Height == height)
				return;

			Dx.Release(this.reactiveUav);
			Dx.Release(this.reactive);
		}

		this.reactive = Dx.CreateTexture(this.device, width, height, DXGI_FORMAT_R8_UNORM, 1, Dx.BindShaderResourceAndUav,
			misc: Dx.MiscSharedNtHandle);
		this.reactiveUav = Dx.CreateUav(this.device, this.reactive, DXGI_FORMAT_R8_UNORM);
		this.reactiveWritten = true; // Undefined until cleared
	}

	/// <summary>
	/// Framework thread. After this call, the hooks pass through and the render thread stops using EffectMask. Wait for
	/// the render thread before Dispose
	/// </summary>
	public void Stop()
	{
		this.stopped = true;
		this.active = false;
	}

	public void Dispose()
	{
		this.stopped = true;
		this.weatherRenderHook?.Dispose();
		this.weatherFinishHook?.Dispose();
		this.effectsMaskEnabledHook?.Dispose();
		this.setRenderTargetsHook?.Dispose();
		if (this.forcedWeather.Count > 0)
		{
			this.forcedWeather.Clear();
			this.RestoreDepthOfField();
		}

		KernelTexture* current = this.Mask;
		this.mask = 0;
		if (current != null)
			current->DecRef();
		Dx.Release(this.reactiveUav);
		Dx.Release(this.reactive);
		Dx.Release(this.constantBuffer);
		Dx.Release(this.shader);
		Dx.Release(this.device);
		GC.SuppressFinalize(this);
	}
}
