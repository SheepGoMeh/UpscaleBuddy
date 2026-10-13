using System;
using System.Runtime.InteropServices;

using Dalamud.Bindings.ImGui;

using TerraFX.Interop.DirectX;

namespace UpscaleBuddy.Game;

/// <summary>
/// Sends Dalamud's overlay into a texture of ours instead of the swap chain's buffer. Dalamud draws it inside the game's Present
/// call; while armed, the first thing on ImGui's background draw list is a callback (Dalamud's renderer runs them inline) that
/// binds our render target on the game's immediate context. ImGui's blend into a transparent target leaves premultiplied alpha,
/// which the presenter lays over every frame it shows.
/// Everything runs where Dalamud's draw does: the main thread, with the render thread idle.
/// </summary>
internal sealed unsafe class OverlayCapture: IDisposable
{
	private static OverlayCapture? current;

	private ID3D11DeviceContext* context;
	private ID3D11RenderTargetView* view;
	private volatile bool armed;

	public OverlayCapture()
	{
		current = this;
		Service.PluginInterface.UiBuilder.Draw += this.OnDraw;
	}

	/// <summary>The next draw Dalamud makes goes into the view (already cleared)</summary>
	public void Arm(ID3D11DeviceContext* immediate, ID3D11RenderTargetView* target)
	{
		this.context = immediate;
		this.view = target;
		this.armed = true;
	}

	public void Disarm() => this.armed = false;

	/// <summary>Inside Dalamud's draw, while the UI is built</summary>
	private void OnDraw()
	{
		if (!this.armed)
			return;

		ImGuiNative.AddCallback(ImGui.GetBackgroundDrawList(), (delegate*<ImDrawList*, ImDrawCmd*, void>)(nint)(delegate* unmanaged<ImDrawList*, ImDrawCmd*, void>)&Redirect, null);
	}

	/// <summary>Dalamud's renderer, on the first command of the background list</summary>
	[UnmanagedCallersOnly]
	private static void Redirect(ImDrawList* list, ImDrawCmd* command)
	{
		OverlayCapture? capture = current;
		if (capture is not { armed: true } || capture.context == null || capture.view == null)
			return;

		ID3D11RenderTargetView* target = capture.view;
		capture.context->OMSetRenderTargets(1, &target, null);
	}

	public void Dispose()
	{
		this.armed = false;
		Service.PluginInterface.UiBuilder.Draw -= this.OnDraw;
		if (current == this)
			current = null;
	}
}
