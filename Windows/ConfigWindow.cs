using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

using UpscaleBuddy.Game;

namespace UpscaleBuddy.Windows;

public class ConfigWindow(UpscaleBuddyConfiguration configuration, DlssPath dlssPath)
	: Window("UpscaleBuddy", ImGuiWindowFlags.AlwaysAutoResize)
{
	private static readonly string[] ModeNames =
	[
		"Off (game's own upscaler)", "Native AA (1.0x)", "Quality (1.5x)", "Balanced (1.7x)", "Performance (2.0x)",
		"Ultra Performance (3.0x)", "Supersample 1.5x (experimental)", "Supersample 2.0x (experimental)",
	];

	public override void Draw()
	{
		ImGui.BeginDisabled(!dlssPath.Available);

		int mode = (int)configuration.Mode;
		if (ImGui.Combo("FSR 3.1 mode", ref mode, ModeNames, ModeNames.Length))
		{
			configuration.Mode = (UpscaleMode)mode;
			configuration.Save();
		}

		if (ImGui.Checkbox("Sharpening (RCAS)", ref configuration.Sharpening))
			configuration.Save();

		ImGui.BeginDisabled(!configuration.Sharpening);
		if (ImGui.SliderFloat("Sharpness", ref configuration.Sharpness, 0.0f, 1.0f))
			configuration.Save();
		ImGui.EndDisabled();

		if (ImGui.Checkbox("Show FSR timings", ref configuration.ShowTimings))
			configuration.Save();

		ImGui.EndDisabled();

		ImGui.TextDisabled("Applies while the game's 3D resolution scaling is set to AMD FSR.");
		ImGui.TextDisabled(dlssPath.Status);
		if (!dlssPath.Running)
			return;

		ImGui.TextDisabled($"Rendering {dlssPath.RenderWidth}x{dlssPath.RenderHeight}, output {dlssPath.OutputWidth}x{dlssPath.OutputHeight}");
		if (configuration.Mode is UpscaleMode.Supersample15 or UpscaleMode.Supersample20)
		{
			string report = dlssPath.TextureReport();
			ImGui.TextDisabled(report);
			if (ImGui.Button("Copy report"))
				ImGui.SetClipboardText(dlssPath.Status + "\n" + report);
		}
		if (configuration.ShowTimings)
			ImGui.TextDisabled($"FSR 3.1: CPU {dlssPath.CpuMs:F3} ms, GPU {dlssPath.GpuMs:F3} ms per frame");
	}
}
