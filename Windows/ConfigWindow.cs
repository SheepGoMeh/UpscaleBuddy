using System;

using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

using UpscaleBuddy.Game;

namespace UpscaleBuddy.Windows;

public class ConfigWindow(UpscaleBuddyConfiguration configuration, DlssPath dlssPath)
	: Window("UpscaleBuddy", ImGuiWindowFlags.AlwaysAutoResize)
{
	// Saved as the enum value, the list order is independent
	private static readonly UpscaleMode[] Modes =
	[
		UpscaleMode.Off, UpscaleMode.NativeAa, UpscaleMode.Quality, UpscaleMode.Balanced, UpscaleMode.Performance,
		UpscaleMode.UltraPerformance, UpscaleMode.Supersample125, UpscaleMode.Supersample15, UpscaleMode.Supersample175,
		UpscaleMode.Supersample20,
	];

	private static readonly string[] ModeNames =
	[
		"Off (game's own upscaler)", "Native AA (1.0x)", "Quality (1.5x)", "Balanced (1.7x)", "Performance (2.0x)",
		"Ultra Performance (3.0x)", "Supersample 1.25x (experimental)", "Supersample 1.5x (experimental)",
		"Supersample 1.75x (experimental)", "Supersample 2.0x (experimental)",
	];

	public override void Draw()
	{
		ImGui.BeginDisabled(!dlssPath.Available);

		int mode = Array.IndexOf(Modes, configuration.Mode);
		if (ImGui.Combo("FSR mode", ref mode, ModeNames, ModeNames.Length))
		{
			configuration.Mode = Modes[mode];
			configuration.Save();
		}

		if (ImGui.Checkbox("Use AMD's FSR DLL over D3D12 (FSR 4 on RDNA 4)", ref configuration.UseAmdDll))
			configuration.Save();

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
		if (configuration.ShowTimings)
			ImGui.TextDisabled($"FSR per frame: CPU {dlssPath.CpuMs:F3} ms, {dlssPath.GpuTimings}");
	}
}
