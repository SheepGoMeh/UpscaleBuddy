using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace UpscaleBuddy;

public class Service
{
	[PluginService] public static IDalamudPluginInterface PluginInterface { get; set; } = null!;

	[PluginService] public static ICommandManager CommandManager { get; set; } = null!;

	[PluginService] public static IFramework Framework { get; set; } = null!;

	[PluginService] public static IGameInteropProvider GameInteropProvider { get; set; } = null!;

	[PluginService] public static ISigScanner SigScanner { get; set; } = null!;

	[PluginService] public static IPluginLog PluginLog { get; set; } = null!;

	[PluginService] public static IGameConfig GameConfig { get; set; } = null!;
}
