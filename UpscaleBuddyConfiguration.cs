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
	Supersample15,
	Supersample20,
	Supersample125,
	Supersample175,
}

public enum Upscaler
{
	BuiltInFsr3,
	AmdFsr,
	IntelXess,
}

public class UpscaleBuddyConfiguration: IPluginConfiguration
{
	public int Version { get; set; }

	public UpscaleMode Mode = UpscaleMode.Quality;
	public bool Sharpening = true;
	public float Sharpness = 0.5f;
	public bool ShowTimings;
	public Upscaler Upscaler = Upscaler.BuiltInFsr3;

	// The D3D12 upscalers open SceneInput/SceneOutput/velocity directly instead of copying them; only shown in Debug builds
	// until it's measured whether shared targets cost the game's passes more than the copies
	public bool ZeroCopy = true;

	// Replaces the game's 60/30 fps limiter with one that waits before input is read
	public bool LowLatencyLimiter;

	// A generated frame before each real one, rendered at half the target
	public bool FrameGeneration;

	// Frames shown per second with frame generation, 0 = the display's refresh rate
	public int FrameGenerationTarget;

	public void Save() => Service.PluginInterface.SavePluginConfig(this);
}
