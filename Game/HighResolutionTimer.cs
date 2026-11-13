using System;
using System.Diagnostics;
using System.Threading;

using TerraFX.Interop.Windows;

using static TerraFX.Interop.Windows.Windows;

namespace UpscaleBuddy.Game;

/// <summary>Sub-millisecond wait for a Stopwatch timestamp. Not thread safe.</summary>
public sealed unsafe class HighResolutionTimer: IDisposable
{
	private static readonly long SpinTicks = Stopwatch.Frequency / 1000;

	private const uint HighResolution = 0x2;
	private const uint TimerAllAccess = 0x1F0003;

	private readonly HANDLE timer = CreateWaitableTimerExW(null, null, HighResolution, TimerAllAccess);

	/// <returns>False when cancel is signalled. Cancel has no effect in the last millisecond</returns>
	public bool Wait(long until, HANDLE cancel = default)
	{
		long now = Stopwatch.GetTimestamp();
		long sleep = until - now - SpinTicks;
		if (sleep > 0)
		{
			if (this.timer.Value != null)
			{
				LARGE_INTEGER due;
				due.QuadPart = -sleep * 10_000_000 / Stopwatch.Frequency;
				SetWaitableTimer(this.timer, &due, 0, null, null, false);
				HANDLE* handles = stackalloc HANDLE[2] { this.timer, cancel };
				if (WaitForMultipleObjects(cancel.Value != null ? 2u : 1u, handles, false, INFINITE) == WAIT.WAIT_OBJECT_0 + 1)
					return false;
			}
			else
			{
				Thread.Sleep(1);
			}

			now = Stopwatch.GetTimestamp();
		}

		while (now < until)
		{
			Thread.SpinWait(16);
			now = Stopwatch.GetTimestamp();
		}

		return true;
	}

	public void Dispose()
	{
		if (this.timer.Value != null)
			CloseHandle(this.timer);
	}
}
