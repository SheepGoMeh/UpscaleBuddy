using System;

using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Windowing;

using UpscaleBuddy.Game;

namespace UpscaleBuddy.Windows;

public class ConfigWindow(UpscaleBuddyConfiguration configuration, DlssPath dlssPath, FramePacer framePacer, UiLayer uiLayer,
	FrameGenerationRunner frameGeneration, ClipboardGuard clipboardGuard, EffectMask effectMask, ReShadeEffects reShade)
	: Window("UpscaleBuddy", ImGuiWindowFlags.AlwaysAutoResize)
{
	// The configuration saves the enum value. The list order is independent of it.
	private static readonly UpscaleMode[] Modes =
	[
		UpscaleMode.Off, UpscaleMode.NativeAa, UpscaleMode.Quality, UpscaleMode.Balanced, UpscaleMode.Performance,
		UpscaleMode.UltraPerformance, UpscaleMode.Supersample125, UpscaleMode.Supersample15, UpscaleMode.Supersample175,
		UpscaleMode.Supersample20,
	];

	private static readonly string[] ModeNames =
	[
		"Off (the game's own upscaler)", "Native AA (100% resolution)", "Quality (67% resolution)", "Balanced (59% resolution)",
		"Performance (50% resolution)", "Ultra Performance (33% resolution)", "Supersample 125% (experimental)",
		"Supersample 150% (experimental)", "Supersample 175% (experimental)", "Supersample 200% (experimental)",
	];

	private static readonly Upscaler[] Upscalers = [Upscaler.BuiltInFsr3, Upscaler.AmdFsr, Upscaler.IntelXess];

	private static readonly string[] UpscalerNames =
	[
		"FSR 3.1 (built in, any GPU)", "AMD FSR (FSR 4 on supported AMD GPUs)", "Intel XeSS",
	];

	// Only on an NVIDIA GPU the game finds DLSS capable
	private static readonly Upscaler[] UpscalersWithDlss = [.. Upscalers, Upscaler.NvidiaDlss];
	private static readonly string[] UpscalerNamesWithDlss = [.. UpscalerNames, "NVIDIA DLSS / DLAA (the game's own)"];

	public override void Draw()
	{
		ImGui.BeginDisabled(!dlssPath.Available);

		Upscaler[] upscalers = dlssPath.NativeDlssSupported ? UpscalersWithDlss : Upscalers;
		string[] upscalerNames = dlssPath.NativeDlssSupported ? UpscalerNamesWithDlss : UpscalerNames;
		int upscaler = Array.IndexOf(upscalers, configuration.Upscaler);
		if (ImGui.Combo("Upscaler", ref upscaler, upscalerNames, upscalerNames.Length))
		{
			configuration.Upscaler = upscalers[upscaler];
			configuration.Save();
		}

		int mode = Array.IndexOf(Modes, configuration.Mode);
		if (ImGui.Combo("Mode", ref mode, ModeNames, ModeNames.Length))
		{
			configuration.Mode = Modes[mode];
			configuration.Save();
		}

		// XeSS has no sharpening pass. The game's own DLSS keeps its own settings.
		bool nvidia = configuration.Upscaler == Upscaler.NvidiaDlss;
		if (nvidia)
			ImGui.TextDisabled("The game's graphics settings choose the DLSS quality and DLAA. Mode only turns DLSS on or off.");
		ImGui.BeginDisabled(configuration.Upscaler == Upscaler.IntelXess || nvidia);
		if (ImGui.Checkbox("Sharpening (FSR only)", ref configuration.Sharpening))
			configuration.Save();

		ImGui.BeginDisabled(!configuration.Sharpening);
		if (ImGui.SliderFloat("Sharpness", ref configuration.Sharpness, 0.0f, 1.0f))
			configuration.Save();
		ImGui.EndDisabled();
		ImGui.EndDisabled();

		ImGui.BeginDisabled(!effectMask.Available);
		if (ImGui.SliderFloat("Rain and effects clarity", ref configuration.EffectResponsiveness, 0.0f, 10.0f, "%.1f"))
			configuration.Save();
		ImGui.EndDisabled();
		if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
		{
			ImGui.SetTooltip(
				"Keeps rain and spell effects clear. Without it, the upscaler can smear them across frames.\n" +
				"Higher values keep more of them. 0 turns this off.\n" +
				"FSR 3.1, XeSS and DLSS use this setting. FSR 4 ignores it.");
		}

		if (ImGui.Checkbox("Show timings", ref configuration.ShowTimings))
			configuration.Save();

#if DEBUG
		// An A/B test. It checks whether shared targets cost the game's passes more than the copies.
		// See UpscaleBuddyConfiguration.ZeroCopy.
		ImGui.BeginDisabled(configuration.Upscaler == Upscaler.BuiltInFsr3);
		if (ImGui.Checkbox("Zero copy (shared render targets)", ref configuration.ZeroCopy))
			configuration.Save();
		ImGui.EndDisabled();
#endif

		ImGui.EndDisabled();

		ImGui.TextDisabled(nvidia ? "Works only while the game's upscaling setting is NVIDIA DLSS." :
			"Works only while the game's upscaling setting is AMD FSR.");
		ImGui.TextDisabled(dlssPath.Status);
		if (dlssPath.Running)
		{
			ImGui.TextDisabled(
				$"The game renders at {dlssPath.RenderWidth}x{dlssPath.RenderHeight}. You see {dlssPath.OutputWidth}x{dlssPath.OutputHeight}.");
			if (configuration.ShowTimings)
				ImGui.TextDisabled($"Upscaler per frame: CPU {dlssPath.CpuMs:F3} ms, {dlssPath.GpuTimings}");
		}

		ImGui.Separator();
		ImGui.BeginDisabled(!clipboardGuard.Available);
		if (ImGui.Checkbox("Prevent freezes when copying (clipboard fix)", ref configuration.SkipClipboardRead))
			configuration.Save();
		if (ImGui.IsItemHovered())
		{
			ImGui.SetTooltip(
				"Stops the game from freezing for a couple of seconds when you copy something in another program.");
		}

		ImGui.EndDisabled();

		if (reShade.Available)
		{
			ImGui.BeginDisabled(!uiLayer.Available);
			if (ImGui.Checkbox("ReShade effects under the UI", ref configuration.ReShadeBeforeUi))
				configuration.Save();
			if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
			{
				ImGui.SetTooltip(
					"ReShade changes only the game world. The UI and nameplates keep their normal look.\n" +
					"The game's brightness setting also applies to ReShade's effects.\n" +
					"With frame generation, ReShade runs once for each rendered frame, not for each shown frame.");
			}

			ImGui.EndDisabled();
		}

		ImGui.BeginDisabled(!framePacer.Available);
		if (ImGui.Checkbox("Low latency frame limiter", ref configuration.LowLatencyLimiter))
			configuration.Save();
		if (ImGui.IsItemHovered())
			ImGui.SetTooltip("Replaces the game's 60 and 30 fps limits. Your input reaches the screen sooner at the same frame rate.");
		ImGui.EndDisabled();

		ImGui.BeginDisabled(!framePacer.Available || !uiLayer.Available);
		if (ImGui.Checkbox("Frame generation", ref configuration.FrameGeneration))
			configuration.Save();
		if (ImGui.IsItemHovered())
		{
			ImGui.SetTooltip(
				"Adds a generated frame between each pair of real frames.\n" +
				"The game renders at half the target frame rate, and you see the full target.\n" +
				"The game's Frame Rate Threshold setting has no effect while this is on.\n" +
				"Needs the upscaler above to be on. Also limits the frame rate like the low latency frame limiter.");
		}

		ImGui.EndDisabled();
		if (!configuration.FrameGeneration)
			return;

		int refresh = framePacer.RefreshRate;
		if (refresh >= 60)
		{
			// A stored 0 means the refresh rate. Thus a different display keeps "refresh rate".
			int target = configuration.FrameGenerationTarget is > 0 and var set && set < refresh ? set : refresh;
			if (ImGui.SliderInt("Target", ref target, 60, refresh, target == refresh ? "%d fps (refresh rate)" : "%d fps"))
			{
				configuration.FrameGenerationTarget = target >= refresh ? 0 : target;
				configuration.Save();
			}

			if (ImGui.IsItemHovered())
			{
				ImGui.SetTooltip(
					"Frames shown per second. The game must render half this number, with time to spare.\n" +
					"A target that does not divide evenly into the refresh rate needs a variable refresh rate display\n" +
					"(G-Sync or FreeSync).");
			}
		}

		if (ImGui.Checkbox("Vsync", ref configuration.FrameGenerationVsync))
			configuration.Save();
		if (ImGui.IsItemHovered())
			ImGui.SetTooltip("On: no tearing. Off: frames can appear a little sooner, but you can see tearing.");

		// Each real frame waits half a rendered frame behind the generated one, plus about 1 ms of generation
		int rendered = framePacer.GenerationRate;
		if (rendered > 0)
		{
			ImGui.TextDisabled(
				$"The game renders {rendered} fps and you see {rendered * 2} fps. Input delay increases by about {(500 / rendered) + 1} ms.");
		}

		if (!framePacer.KeepingUp)
		{
			int sustained = framePacer.SustainedTarget;
			ImGui.TextColored(ImGuiColors.DalamudOrange, sustained >= 60
				? $"Too slow for this target. This scene holds about {sustained} fps. Lower the target."
				: "The game is too slow here for frame generation.");
		}

		ImGui.TextDisabled(frameGeneration.Paused
			? "Paused while you are AFK or the game window is in the background."
			: frameGeneration.Status);
	}
}
