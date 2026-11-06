using System;

using Dalamud.Hooking;

using FFXIVClientStructs.FFXIV.Client.UI;

namespace UpscaleBuddy.Game;

/// <summary>
/// The game is a clipboard viewer.
/// On every clipboard change, its window procedure calls UIClipboard.OnClipboardDataChanged.
/// That function opens the clipboard and reads its text on the main thread.
/// Then it gives the text to a function that does nothing.
/// When a tool such as ShareX copies a screenshot's path, the read deadlocks with the tool's OleFlushClipboard.
/// OleFlushClipboard retries for 2 s until the clipboard is free, while the game waits on the tool.
/// The game stops for 2 s.
/// The detour skips the read, so the game does not stop. The game does not use the text.
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
