using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

using Dalamud.Hooking;

using FFXIVClientStructs.FFXIV.Client.Graphics.Kernel;

using TerraFX.Interop.DirectX;

using static TerraFX.Interop.DirectX.DXGI_FORMAT;

using KernelTexture = FFXIVClientStructs.FFXIV.Client.Graphics.Kernel.Texture;

namespace UpscaleBuddy.Game;

/// <summary>
/// Applies ReShade's effects to the scene before the UI, not to the back buffer at Present. Uses ReShade's add-on API
/// (effect_runtime::render_effects). In a frame that had this call, ReShade's Present skips its own pass.
/// - The plugin loads in memory, so ReShade cannot find a module for it. Dalamud's own add-on has the same problem.
///   Hooks on ReShade's GetModuleHandleExW and GetModuleFileNameW imports map our handle and callbacks to the plugin.
///   They also give ReShade the plugin's file, which supplies the add-on's name.
/// - Uses API version 10 (ReShade 6.0). The vtable slots below are for that version.
/// - Registers only the events that the standard ReShade build allows for add-ons: reshade_present and
///   destroy_effect_runtime.
/// </summary>
public unsafe class ReShadeEffects: IDisposable
{
	private const uint ApiVersion = 10;
	private const uint EventDestroyEffectRuntime = 10;
	private const uint EventReShadePresent = 75;

	// effect_runtime: api_object (3), device_object::get_device, then get_hwnd ... render_effects
	private const int GetHwndSlot = 4;
	private const int GetCommandQueueSlot = 8;
	private const int RenderEffectsSlot = 9;

	// command_queue: api_object (3), device_object::get_device, get_type, wait_idle, flush_immediate_command_list
	private const int GetImmediateCommandListSlot = 7;

	private const uint FromAddress = 0x4; // GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS

	private delegate int GetModuleHandleExWDelegate(uint flags, char* name, nint* module);
	private delegate uint GetModuleFileNameWDelegate(nint module, char* buffer, uint size);
	private delegate void RuntimeDelegate(nint runtime);

	private readonly UpscaleBuddyConfiguration configuration;
	private readonly nint handle;
	private readonly Hook<GetModuleHandleExWDelegate>? moduleHandleHook;
	private readonly Hook<GetModuleFileNameWDelegate>? moduleFileNameHook;
	private readonly delegate* unmanaged<nint, void> unregisterAddon;

	// The delegates stay alive here because ReShade calls them at Present
	private readonly RuntimeDelegate presentCallback;
	private readonly RuntimeDelegate destroyCallback;
	private readonly nint presentFunction;
	private readonly nint destroyFunction;
	private readonly string path;
	private readonly bool registered;

	// The runtime of the game window. OnPresent sets it and OnDestroy clears it
	private volatile nint runtime;

	// 1 after Render applies the effects. The next Present of the game's swap chain sets it to 0
	private int rendered;

	// Render thread. The sRGB view of the last target, for effect passes that use SRGBWriteEnable
	private ID3D11Texture2D* viewTexture;
	private ID3D11RenderTargetView* srgbView;

	public ReShadeEffects(UpscaleBuddyConfiguration configuration)
	{
		this.configuration = configuration;
		this.presentCallback = this.OnPresent;
		this.destroyCallback = this.OnDestroy;
		this.presentFunction = Marshal.GetFunctionPointerForDelegate(this.presentCallback);
		this.destroyFunction = Marshal.GetFunctionPointerForDelegate(this.destroyCallback);
		this.path = Service.PluginInterface.AssemblyLocation.FullName;
		this.handle = Marshal.GetHINSTANCE(typeof(ReShadeEffects).Assembly.ManifestModule);

		ProcessModule? module = FindReShade();
		if (module == null)
			return;

		if (this.handle is 0 or -1)
		{
			Service.PluginLog.Warning("ReShade found, but the plugin has no module handle to register with");
			return;
		}

		try
		{
			nint library = module.BaseAddress;
			var registerAddon = (delegate* unmanaged<nint, uint, byte>)NativeLibrary.GetExport(library, "ReShadeRegisterAddon");
			var registerEvent = (delegate* unmanaged<uint, nint, void>)NativeLibrary.GetExport(library, "ReShadeRegisterEvent");
			this.unregisterAddon = (delegate* unmanaged<nint, void>)NativeLibrary.GetExport(library, "ReShadeUnregisterAddon");

			this.moduleHandleHook = Service.GameInteropProvider.HookFromImport<GetModuleHandleExWDelegate>(
				module, "kernel32.dll", "GetModuleHandleExW", 0, this.GetModuleHandleExWDetour);
			this.moduleFileNameHook = Service.GameInteropProvider.HookFromImport<GetModuleFileNameWDelegate>(
				module, "kernel32.dll", "GetModuleFileNameW", 0, this.GetModuleFileNameWDetour);
			this.moduleHandleHook.Enable();
			this.moduleFileNameHook.Enable();

			if (registerAddon(this.handle, ApiVersion) == 0)
			{
				Service.PluginLog.Warning($"ReShade {module.FileVersionInfo.FileVersion} refused the add-on (needs 6.0 or later), see ReShade.log");
				return;
			}

			this.registered = true;
			registerEvent(EventReShadePresent, this.presentFunction);
			registerEvent(EventDestroyEffectRuntime, this.destroyFunction);
			this.Available = true;
			Service.PluginLog.Information($"Registered with ReShade {module.FileVersionInfo.FileVersion} ({module.FileName})");
		}
		catch (Exception e)
		{
			Service.PluginLog.Warning(e, "ReShade found, registering with it failed");
		}
	}

	/// <summary>True when ReShade is loaded and accepted the add-on</summary>
	public bool Available { get; }

	/// <summary>
	/// Serializes render_effects with Presents off the main thread. ReShade deadlocks when they overlap
	/// </summary>
	public object PresentLock { get; } = new();

	/// <summary>Main thread. True when the effects go on the scene before the UI in this frame</summary>
	public bool Active => this.configuration.ReShadeBeforeUi && this.runtime != 0;

	/// <summary>
	/// Render thread. Applies the effects to the target. The target contains the finished scene without the UI
	/// </summary>
	public void Render(KernelTexture* target)
	{
		nint current = this.runtime;
		if (current == 0 || !this.configuration.ReShadeBeforeUi)
			return;

		ID3D11RenderTargetView* view = (ID3D11RenderTargetView*)target->GetMipRenderTarget(0)->D3D11RenderTargetViewOrDepthStencilView;
		ID3D11RenderTargetView* srgb = this.SrgbView((ID3D11Texture2D*)target->D3D11Texture2D);
		lock (this.PresentLock)
			RenderEffects(current, (nint)view, (nint)(srgb != null ? srgb : view));
		Interlocked.Exchange(ref this.rendered, 1);
	}

	/// <summary>
	/// Main thread, at the game's Present. Returns true when Render applied the effects to this frame. Then clears the
	/// flag
	/// </summary>
	public bool TakeRendered() => Interlocked.Exchange(ref this.rendered, 0) != 0;

	/// <summary>
	/// Makes ReShade skip its pass at the next Present, for frames that already have the effects
	/// </summary>
	public void SkipNextPresent()
	{
		nint current = this.runtime;
		if (current == 0)
			return;

		lock (this.PresentLock)
			RenderEffects(current, 0, 0);
	}

	private static void RenderEffects(nint runtime, nint view, nint srgbView)
	{
		nint* vtable = *(nint**)runtime;
		nint queue = ((delegate* unmanaged<nint, nint>)vtable[GetCommandQueueSlot])(runtime);
		nint commandList = ((delegate* unmanaged<nint, nint>)(*(nint**)queue)[GetImmediateCommandListSlot])(queue);
		((delegate* unmanaged<nint, nint, nint, nint, void>)vtable[RenderEffectsSlot])(runtime, commandList, view, srgbView);
	}

	/// <summary>
	/// Returns an sRGB view when the target's format is typeless, or null otherwise. With null, Render uses the
	/// target's own view for both
	/// </summary>
	private ID3D11RenderTargetView* SrgbView(ID3D11Texture2D* texture)
	{
		if (texture == this.viewTexture)
			return this.srgbView;

		Dx.Release(this.srgbView);
		this.srgbView = null;
		this.viewTexture = texture;

		DXGI_FORMAT format = Dx.GetDesc(texture).Format switch
		{
			DXGI_FORMAT_R8G8B8A8_TYPELESS => DXGI_FORMAT_R8G8B8A8_UNORM_SRGB,
			DXGI_FORMAT_B8G8R8A8_TYPELESS => DXGI_FORMAT_B8G8R8A8_UNORM_SRGB,
			_ => DXGI_FORMAT_UNKNOWN,
		};
		if (format == DXGI_FORMAT_UNKNOWN)
			return null;

		ID3D11Device* device;
		texture->GetDevice(&device);
		D3D11_RENDER_TARGET_VIEW_DESC desc = new() { Format = format, ViewDimension = D3D11_RTV_DIMENSION.D3D11_RTV_DIMENSION_TEXTURE2D };
		ID3D11RenderTargetView* view;
		if (device->CreateRenderTargetView((ID3D11Resource*)texture, &desc, &view) >= 0)
			this.srgbView = view;
		device->Release();
		return this.srgbView;
	}

	/// <summary>
	/// ReShade calls this at the Present of each runtime, Dalamud's viewports included. Keeps the game window's runtime
	/// </summary>
	private void OnPresent(nint current)
	{
		if (current == this.runtime)
			return;

		nint hwnd = ((delegate* unmanaged<nint, nint>)(*(nint**)current)[GetHwndSlot])(current);
		Device* device = Device.Instance();
		if (device != null && hwnd == (nint)device->hWnd)
			this.runtime = current;
	}

	private void OnDestroy(nint current)
	{
		if (current == this.runtime)
			this.runtime = 0;
	}

	private int GetModuleHandleExWDetour(uint flags, char* name, nint* module)
	{
		nint address = (nint)name;
		if ((flags & FromAddress) != 0 && (address == this.handle || address == this.presentFunction || address == this.destroyFunction))
		{
			*module = this.handle;
			return 1;
		}

		return this.moduleHandleHook!.Original(flags, name, module);
	}

	private uint GetModuleFileNameWDetour(nint module, char* buffer, uint size)
	{
		if (module != this.handle || size == 0)
			return this.moduleFileNameHook!.Original(module, buffer, size);

		int length = Math.Min(this.path.Length, (int)size - 1);
		this.path.AsSpan(0, length).CopyTo(new Span<char>(buffer, (int)size));
		buffer[length] = '\0';
		return (uint)length;
	}

	/// <summary>Returns the module with ReShade's add-on exports. Dalamud picks the same module</summary>
	private static ProcessModule? FindReShade()
	{
		foreach (ProcessModule module in Process.GetCurrentProcess().Modules)
		{
			if (NativeLibrary.TryGetExport(module.BaseAddress, "ReShadeRegisterAddon", out _) &&
			    NativeLibrary.TryGetExport(module.BaseAddress, "ReShadeRegisterEvent", out _) &&
			    NativeLibrary.TryGetExport(module.BaseAddress, "ReShadeUnregisterAddon", out _))
				return module;
		}

		return null;
	}

	/// <summary>
	/// Framework thread, after the render thread stops calling Render. Unregisters before it removes the hooks, because
	/// ReShade finds the add-on through them
	/// </summary>
	public void Dispose()
	{
		this.runtime = 0;
		if (this.registered)
			this.unregisterAddon(this.handle);
		this.moduleFileNameHook?.Dispose();
		this.moduleHandleHook?.Dispose();
		Dx.Release(this.srgbView);
		this.srgbView = null;
		GC.SuppressFinalize(this);
	}
}
