using System;

using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Windowing;

using UpscaleBuddy.Game;

namespace UpscaleBuddy.Windows;

public class ConfigWindow(UpscaleBuddyConfiguration configuration, DlssPath dlssPath, FramePacer framePacer, UiLayer uiLayer,
	FrameGenerationRunner frameGeneration)
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

	private static readonly Upscaler[] Upscalers = [Upscaler.BuiltInFsr3, Upscaler.AmdFsr, Upscaler.IntelXess];

	private static readonly string[] UpscalerNames =
	[
		"FSR 3.1 (built-in, D3D11)", "AMD FSR (DLL over D3D12, FSR 4 where supported)", "Intel XeSS (DLL over D3D12)",
	];

	public override void Draw()
	{
		ImGui.BeginDisabled(!dlssPath.Available);

		int upscaler = Array.IndexOf(Upscalers, configuration.Upscaler);
		if (ImGui.Combo("Upscaler", ref upscaler, UpscalerNames, UpscalerNames.Length))
		{
			configuration.Upscaler = Upscalers[upscaler];
			configuration.Save();
		}

		int mode = Array.IndexOf(Modes, configuration.Mode);
		if (ImGui.Combo("Mode", ref mode, ModeNames, ModeNames.Length))
		{
			configuration.Mode = Modes[mode];
			configuration.Save();
		}

		// XeSS has no sharpening pass
		ImGui.BeginDisabled(configuration.Upscaler == Upscaler.IntelXess);
		if (ImGui.Checkbox("Sharpening (RCAS, FSR only)", ref configuration.Sharpening))
			configuration.Save();

		ImGui.BeginDisabled(!configuration.Sharpening);
		if (ImGui.SliderFloat("Sharpness", ref configuration.Sharpness, 0.0f, 1.0f))
			configuration.Save();
		ImGui.EndDisabled();
		ImGui.EndDisabled();

		if (ImGui.Checkbox("Show timings", ref configuration.ShowTimings))
			configuration.Save();

#if DEBUG
		// A/B for whether shared targets cost the game's passes more than the copies, see UpscaleBuddyConfiguration.ZeroCopy
		ImGui.BeginDisabled(configuration.Upscaler == Upscaler.BuiltInFsr3);
		if (ImGui.Checkbox("Zero copy (shared render targets)", ref configuration.ZeroCopy))
			configuration.Save();
		ImGui.EndDisabled();
#endif

		ImGui.EndDisabled();

		ImGui.TextDisabled("Applies while the game's 3D resolution scaling is set to AMD FSR.");
		ImGui.TextDisabled(dlssPath.Status);
		if (dlssPath.Running)
		{
			ImGui.TextDisabled($"Rendering {dlssPath.RenderWidth}x{dlssPath.RenderHeight}, output {dlssPath.OutputWidth}x{dlssPath.OutputHeight}");
			if (configuration.ShowTimings)
				ImGui.TextDisabled($"Upscaler per frame: CPU {dlssPath.CpuMs:F3} ms, {dlssPath.GpuTimings}");
		}

		ImGui.Separator();
		ImGui.BeginDisabled(!framePacer.Available);
		if (ImGui.Checkbox("Low latency frame limiter", ref configuration.LowLatencyLimiter))
			configuration.Save();
		if (ImGui.IsItemHovered())
			ImGui.SetTooltip("Replaces the game's 60/30 fps limit: waits before input is read instead of after the frame is built.");
		ImGui.EndDisabled();

		ImGui.BeginDisabled(!framePacer.Available || !uiLayer.Available);
		if (ImGui.Checkbox("Frame generation", ref configuration.FrameGeneration))
			configuration.Save();
		if (ImGui.IsItemHovered())
		{
			ImGui.SetTooltip(
				"Generates a frame between every two rendered ones: the game renders at half the target and frames are\n" +
				"shown at the target. The game's Frame Rate Threshold doesn't apply while this is on. Needs the upscaler\n" +
				"above running, and pacing like the low latency frame limiter is always on.");
		}

		ImGui.EndDisabled();
		if (!configuration.FrameGeneration)
			return;

		int refresh = framePacer.RefreshRate;
		if (refresh >= 60)
		{
			// 0 stored = the refresh rate, so a different display keeps "refresh rate"
			int target = configuration.FrameGenerationTarget is > 0 and var set && set < refresh ? set : refresh;
			if (ImGui.SliderInt("Target", ref target, 60, refresh, target == refresh ? "%d fps (refresh rate)" : "%d fps"))
			{
				configuration.FrameGenerationTarget = target >= refresh ? 0 : target;
				configuration.Save();
			}

			if (ImGui.IsItemHovered())
			{
				ImGui.SetTooltip(
					"Frames shown per second. The game has to render half of it, with headroom: its frame has to be built\n" +
					"in half a rendered frame. Targets that don't divide the refresh rate need a variable refresh display.");
			}
		}

		// Each real frame waits half a rendered frame behind the generated one, plus about 1 ms of generation
		int rendered = framePacer.GenerationRate;
		if (rendered > 0)
			ImGui.TextDisabled($"Rendering {rendered} fps, showing {rendered * 2} fps, adds about {(500 / rendered) + 1} ms of latency");
		if (!framePacer.KeepingUp)
		{
			int sustained = framePacer.SustainedTarget;
			ImGui.TextColored(ImGuiColors.DalamudOrange, sustained >= 60
				? $"Can't keep up: this scene holds about {sustained} fps, lower the target"
				: "Can't keep up: the game is too slow here for frame generation");
		}

		ImGui.TextDisabled(frameGeneration.Status);
	}
}
