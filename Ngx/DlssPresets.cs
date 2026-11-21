namespace UpscaleBuddy.Ngx;

/// <summary>The DLSS render preset hints that NGX reads at CreateFeature, one for each quality mode.</summary>
public static unsafe class DlssPresets
{
	private static readonly string[] Hints =
	[
		"DLSS.Hint.Render.Preset.DLAA", "DLSS.Hint.Render.Preset.Quality", "DLSS.Hint.Render.Preset.Balanced",
		"DLSS.Hint.Render.Preset.Performance", "DLSS.Hint.Render.Preset.UltraPerformance", "DLSS.Hint.Render.Preset.UltraQuality",
	];

	/// <summary>Sets preset on every hint. This replaces the preset that the game writes.</summary>
	public static void Apply(NgxParameter* parameters, DlssPreset preset)
	{
		foreach (string hint in Hints)
			parameters->SetUInt(hint, (uint)preset);
	}
}
