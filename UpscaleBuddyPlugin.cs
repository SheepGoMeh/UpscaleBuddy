using System;
using System.Threading;

using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

using UpscaleBuddy.Game;
using UpscaleBuddy.Windows;

namespace UpscaleBuddy;

public class UpscaleBuddyPlugin: IDalamudPlugin
{
	private const string CommandName = "/upscalebuddy";

	private readonly UpscaleBuddyConfiguration configuration;
	private readonly DlssPath dlssPath;
	private readonly WindowSystem windowSystem;
	private readonly ConfigWindow configWindow;

	public UpscaleBuddyPlugin(IDalamudPluginInterface pluginInterface)
	{
		pluginInterface.Create<Service>();

		this.configuration = Service.PluginInterface.GetPluginConfig() as UpscaleBuddyConfiguration ??
		                     new UpscaleBuddyConfiguration();

		this.dlssPath = new DlssPath(this.configuration);

		this.windowSystem = new WindowSystem("UpscaleBuddy");
		this.configWindow = new ConfigWindow(this.configuration, this.dlssPath);
		this.windowSystem.AddWindow(this.configWindow);

		Service.PluginInterface.UiBuilder.Draw += this.windowSystem.Draw;
		Service.PluginInterface.UiBuilder.OpenConfigUi += this.configWindow.Toggle;
		Service.CommandManager.AddHandler(
			CommandName,
			new CommandInfo((_, _) => this.configWindow.Toggle()) { HelpMessage = "Open the UpscaleBuddy settings." });
		Service.Framework.Update += this.OnFrameworkUpdate;
	}

	private void OnFrameworkUpdate(IFramework framework) => this.dlssPath.Update();

	protected virtual void Dispose(bool disposing)
	{
		if (!disposing)
		{
			return;
		}

		Service.Framework.Update -= this.OnFrameworkUpdate;
		Service.CommandManager.RemoveHandler(CommandName);
		Service.PluginInterface.UiBuilder.Draw -= this.windowSystem.Draw;
		Service.PluginInterface.UiBuilder.OpenConfigUi -= this.configWindow.Toggle;
		this.windowSystem.RemoveAllWindows();

		// Give queued render commands time to finish before tearing down
		Service.Framework.RunOnFrameworkThread(this.dlssPath.Stop).Wait();
		Thread.Sleep(200);
		Service.Framework.RunOnFrameworkThread(this.dlssPath.Finish).Wait();
		this.dlssPath.Dispose();
	}

	public void Dispose()
	{
		this.Dispose(true);
		GC.SuppressFinalize(this);
	}
}
