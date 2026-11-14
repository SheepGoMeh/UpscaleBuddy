using System;
using System.Runtime.InteropServices;
using System.Threading;

using Dalamud.Bindings.ImGui;

using TerraFX.Interop.DirectX;

namespace UpscaleBuddy.Game;

/// <summary>Use only inside the game's Present</summary>
internal sealed unsafe class OverlayCapture: IDisposable
{
	private static OverlayCapture? current;

	private ID3D11DeviceContext* context;
	private ID3D11RenderTargetView* view;
	private volatile bool armed;
	private int draws;

	public OverlayCapture()
	{
		current = this;
		Service.PluginInterface.UiBuilder.Draw += this.OnDraw;
	}

	/// <summary>Keeps OnDraw running while Dalamud hides plugin UI. Required while capturing</summary>
	public void KeepDrawing(bool keep)
	{
		Service.PluginInterface.UiBuilder.DisableAutomaticUiHide = keep;
		Service.PluginInterface.UiBuilder.DisableCutsceneUiHide = keep;
		Service.PluginInterface.UiBuilder.DisableGposeUiHide = keep;
		Service.PluginInterface.UiBuilder.DisableUserUiHide = keep;
	}

	/// <summary>The view must be clear</summary>
	public void Arm(ID3D11DeviceContext* immediate, ID3D11RenderTargetView* target)
	{
		this.context = immediate;
		this.view = target;
		this.armed = true;
	}

	public void Disarm() => this.armed = false;

	/// <summary>The number of Dalamud frames that called OnDraw. Dalamud skips OnDraw while it hides plugin UI, unless
	/// KeepDrawing is on</summary>
	public int Draws => Volatile.Read(ref this.draws);

	private void OnDraw()
	{
		Interlocked.Increment(ref this.draws);
		if (!this.armed)
			return;

		ImGuiNative.AddCallback(ImGui.GetBackgroundDrawList(),
			(delegate*<ImDrawList*, ImDrawCmd*, void>)(nint)(delegate* unmanaged<ImDrawList*, ImDrawCmd*, void>)&Redirect, null);
	}

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
		this.KeepDrawing(false);
		Service.PluginInterface.UiBuilder.Draw -= this.OnDraw;
		if (current == this)
			current = null;
	}
}
