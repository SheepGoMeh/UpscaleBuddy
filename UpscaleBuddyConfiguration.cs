using Dalamud.Configuration;

namespace UpscaleBuddy;

public enum UpscaleMode
{
	Off,
	NativeAa,
	Quality,
	Balanced,
	Performance,
	UltraPerformance,
}

public class UpscaleBuddyConfiguration: IPluginConfiguration
{
	public int Version { get; set; }

	public UpscaleMode Mode = UpscaleMode.Quality;
	public bool Sharpening = true;
	public float Sharpness = 0.5f;
	public bool ShowTimings;

	public void Save() => Service.PluginInterface.SavePluginConfig(this);
}
