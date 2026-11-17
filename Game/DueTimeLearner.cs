using System;

namespace UpscaleBuddy.Game;

public sealed class DueTimeLearner(int window = 120, double percentile = 0.95, double marginMs = 0.5)
{
	private readonly double[] offsets = new double[window];
	private readonly double[] sorted = new double[window];
	private int count;
	private int next;

	/// <summary>offsetMs is in milliseconds. It can be negative.</summary>
	public void Add(double offsetMs)
	{
		this.offsets[this.next] = offsetMs;
		this.next = (this.next + 1) % this.offsets.Length;
		this.count = Math.Min(this.count + 1, this.offsets.Length);
	}

	public double LagMs(double periodMs)
	{
		double high = 0;
		if (this.count > 0)
		{
			Span<double> recent = this.sorted.AsSpan(0, this.count);
			this.offsets.AsSpan(0, this.count).CopyTo(recent);
			recent.Sort();
			high = recent[(int)Math.Ceiling(percentile * this.count) - 1];
		}

		return Math.Clamp(high + marginMs, 0, Math.Max(0, (periodMs / 2) - 1));
	}
}
