using System;

using Dalamud.Hooking;

using FFXIVClientStructs.FFXIV.Client.UI;

namespace UpscaleBuddy.Game;

/// <summary>
/// The game is a clipboard viewer: on every clipboard change its window procedure calls UIClipboard.OnClipboardDataChanged, which
/// opens the clipboard and reads its text on the main thread, and then hands it to a function that does nothing. When a tool such
/// as ShareX copies a screenshot's path it deadlocks with the tool's OleFlushClipboard, which retries for 2 s until the clipboard is
/// free while the game waits on the tool: the game stops for 2 s. Skipping the read ends that, the text isn't used
/// </summary>
public unsafe class ClipboardGuard: IDisposable
{
	private readonly UpscaleBuddyConfiguration configuration;
	private readonly Hook<UIClipboard.Delegates.OnClipboardDataChanged>? hook;

	public ClipboardGuard(UpscaleBuddyConfiguration configuration)
	{
		this.configuration = configuration;
		try
		{
			this.hook = Service.GameInteropProvider.HookFromAddress<UIClipboard.Delegates.OnClipboardDataChanged>(
				(nint)UIClipboard.MemberFunctionPointers.OnClipboardDataChanged, this.OnClipboardDataChangedDetour);
			this.hook.Enable();
			this.Available = true;
		}
		catch (Exception e)
		{
			Service.PluginLog.Warning(e, "ClipboardGuard unavailable");
		}
	}

	public bool Available { get; }

	private void OnClipboardDataChangedDetour(UIClipboard* clipboard)
	{
		if (!this.configuration.SkipClipboardRead)
			this.hook!.Original(clipboard);
	}

	public void Dispose()
	{
		this.hook?.Dispose();
		GC.SuppressFinalize(this);
	}
}
