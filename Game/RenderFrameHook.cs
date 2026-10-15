using System;
using System.Diagnostics;

using Dalamud.Hooking;

using FFXIVClientStructs.FFXIV.Client.Graphics.Kernel;

namespace UpscaleBuddy.Game;

/// <summary>
/// The render thread's frame: ImmediateContext.ProcessCommands runs all of a frame's recorded commands between the main thread's
/// kick and the render-done signal it sets right after. Handlers run on the render thread; the main thread waits for Finished's
/// handlers before it can start the next frame, so they have to stay cheap. After the last command the game's final output
/// is complete and the context is still the render thread's.
/// </summary>
public unsafe class RenderFrameHook: IDisposable
{
	private readonly Hook<ImmediateContext.Delegates.ProcessCommands>? hook;

	public RenderFrameHook()
	{
		try
		{
			this.hook = Service.GameInteropProvider.HookFromAddress<ImmediateContext.Delegates.ProcessCommands>(
				(nint)ImmediateContext.MemberFunctionPointers.ProcessCommands, this.ProcessCommandsDetour);
			this.hook.Enable();
			this.Available = true;
		}
		catch (Exception e)
		{
			Service.PluginLog.Warning(e, "RenderFrameHook unavailable");
		}
	}

	public bool Available { get; }

	/// <summary>The kick: the frame's commands are about to run, with the time</summary>
	public event Action<long>? Started;

	/// <summary>The frame's last command ran, with the time taken right after it</summary>
	public event Action<long>? Finished;

	private void ProcessCommandsDetour(ImmediateContext* context, RenderCommandBufferGroup* commands, uint count)
	{
		this.Started?.Invoke(Stopwatch.GetTimestamp());
		this.hook!.Original(context, commands, count);
		this.Finished?.Invoke(Stopwatch.GetTimestamp());
	}

	public void Dispose()
	{
		this.hook?.Dispose();
		GC.SuppressFinalize(this);
	}
}
