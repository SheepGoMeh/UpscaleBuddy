#if DEBUG
using System;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Threading;

namespace UpscaleBuddy;

/// <summary>
/// Debug builds only. Measures CPU time and allocated bytes at fixed points, and makes one report line every 10 s.
/// The probes don't allocate. Report does, so call it outside the measured code.
/// Build with -p:Optimize=true to get representative times.
/// </summary>
public static class PerfLog
{
	public enum Probe
	{
		Upscale, // Render thread, the whole NGX evaluate. One call per rendered frame
		FrameGenerationSync, // Render thread
		FrameGenerationSubmit, // Render thread, async
		FrameGenerationWorker, // Worker thread, async. One call per job
		NgxParameter, // One call per parameter the game reads or writes
		Count,
	}

	private const double WindowSeconds = 10;
	private const int MaxSamples = 8192; // Per window and series, for p99 and max

	private sealed class Series
	{
		private long calls, ticks, bytes;
		private long[] samples = new long[MaxSamples], spare = new long[MaxSamples];
		private int sampled;

		public void Add(long elapsed, long allocated)
		{
			Interlocked.Increment(ref this.calls);
			Interlocked.Add(ref this.ticks, elapsed);
			Interlocked.Add(ref this.bytes, allocated);
			int index = Interlocked.Increment(ref this.sampled) - 1;
			if (index >= 0 && index < MaxSamples)
				this.samples[index] = elapsed;
		}

		/// <summary>Returns the window so far and starts the next one. A call during Take can land in either</summary>
		public Window Take()
		{
			long takenCalls = Interlocked.Exchange(ref this.calls, 0);
			long takenTicks = Interlocked.Exchange(ref this.ticks, 0);
			long takenBytes = Interlocked.Exchange(ref this.bytes, 0);
			int count = Math.Min(Interlocked.Exchange(ref this.sampled, 0), MaxSamples);
			long[] taken = this.samples;
			this.samples = this.spare;
			this.spare = taken;
			Array.Sort(taken, 0, count);
			return new Window(takenCalls, takenTicks, takenBytes, count > 0 ? taken[(((count * 99) + 99) / 100) - 1] : 0,
				count > 0 ? taken[count - 1] : 0);
		}
	}

	private readonly record struct Window(long Calls, long Ticks, long Bytes, long P99, long Max);

	public readonly ref struct Scope
	{
		private readonly Probe probe;
		private readonly long bytes, start;

		public Scope(Probe probe)
		{
			this.probe = probe;
			this.bytes = GC.GetAllocatedBytesForCurrentThread();
			this.start = Stopwatch.GetTimestamp();
		}

		public void Dispose()
		{
			long elapsed = Stopwatch.GetTimestamp() - this.start;
			series[(int)this.probe].Add(elapsed, GC.GetAllocatedBytesForCurrentThread() - this.bytes);
		}
	}

	private static readonly Series[] series = Create();
	private static readonly Series frameIntervals = new();
	private static readonly Series workerAllocations = new();
	private static long lastFrame;
	private static long windowStart;
	private static int collections;

	[ThreadStatic]
	private static long workerBytes;

	public static Scope Measure(Probe probe)
	{
		if (probe == Probe.Upscale)
		{
			long now = Stopwatch.GetTimestamp();
			long previous = Interlocked.Exchange(ref lastFrame, now);
			if (previous != 0)
				frameIntervals.Add(now - previous, 0);
		}

		return new Scope(probe);
	}

	/// <summary>
	/// Call at the start of each worker job. Counts everything the thread allocated since its previous job, which
	/// includes what the compiler allocates before the first statement of the job.
	/// </summary>
	public static void WorkerJob()
	{
		long now = GC.GetAllocatedBytesForCurrentThread();
		if (workerBytes != 0)
			workerAllocations.Add(0, now - workerBytes);
		workerBytes = now;
	}

	/// <summary>Call from one thread. Returns the report when a window has ended, otherwise null</summary>
	public static string? Report(Version? version, string upscaler, string frameGeneration)
	{
		long now = Stopwatch.GetTimestamp();
		if (windowStart == 0)
		{
			windowStart = now;
			collections = GC.CollectionCount(0);
			return null;
		}

		double seconds = Stopwatch.GetElapsedTime(windowStart, now).TotalSeconds;
		if (seconds < WindowSeconds)
			return null;

		windowStart = now;
		int collected = GC.CollectionCount(0);
		int gen0 = collected - collections;
		collections = collected;

		Window frames = frameIntervals.Take();
		Window worker = workerAllocations.Take();
		Window[] windows = new Window[(int)Probe.Count];
		for (int i = 0; i < windows.Length; i++)
			windows[i] = series[i].Take();

		long rendered = windows[(int)Probe.Upscale].Calls;
		StringBuilder line = new();
		line.Append(CultureInfo.InvariantCulture, $"PERF {version} {seconds:F1} s | frames {rendered} ({rendered / seconds:F1}/s)");
		if (frames.Calls > 0)
		{
			line.Append(CultureInfo.InvariantCulture, $", interval ms avg {Ms(frames.Ticks) / frames.Calls:F2} p99 {Ms(frames.P99):F2} max {Ms(frames.Max):F2}");
		}

		Describe(line, "upscale", windows[(int)Probe.Upscale], null);
		Describe(line, "fg sync", windows[(int)Probe.FrameGenerationSync], null);
		Describe(line, "fg submit", windows[(int)Probe.FrameGenerationSubmit], null);
		Describe(line, "fg worker", windows[(int)Probe.FrameGenerationWorker], worker.Calls > 0 ? (double)worker.Bytes / worker.Calls : 0);

		Window ngx = windows[(int)Probe.NgxParameter];
		if (ngx.Calls > 0 && rendered > 0)
		{
			line.Append(CultureInfo.InvariantCulture, $" | ngx {(double)ngx.Calls / rendered:F1} calls/frame, {Ms(ngx.Ticks) * 1000 / rendered:F2} us/frame, " +
				$"{(double)ngx.Bytes / rendered:F0} B/frame");
		}

		line.Append(CultureInfo.InvariantCulture, $" | gen0 {gen0} (whole process) | upscaler \"{upscaler}\" | frame generation \"{frameGeneration}\"");
		return line.ToString();
	}

	private static void Describe(StringBuilder line, string label, in Window window, double? bytesPerCall)
	{
		if (window.Calls == 0)
			return;

		line.Append(CultureInfo.InvariantCulture, $" | {label} {window.Calls}x avg {Ms(window.Ticks) * 1000 / window.Calls:F1} us p99 {Ms(window.P99) * 1000:F1} us, " +
			$"{bytesPerCall ?? ((double)window.Bytes / window.Calls):F0} B");
	}

	private static double Ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;

	private static Series[] Create()
	{
		Series[] created = new Series[(int)Probe.Count];
		for (int i = 0; i < created.Length; i++)
			created[i] = new Series();
		return created;
	}
}
#endif
