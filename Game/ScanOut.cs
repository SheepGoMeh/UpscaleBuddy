using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

using TerraFX.Interop.Windows;

using static TerraFX.Interop.Windows.Windows;

using Win32Error = TerraFX.Interop.Windows.ERROR;

namespace UpscaleBuddy.Game;

/// <summary>Present times one refresh apart. First and the result of Next are Stopwatch timestamps.</summary>
public readonly record struct VBlankGrid(long First, double Period)
{
	/// <summary>The first grid time at or after time</summary>
	public long Next(long time) => (long)Math.Ceiling(this.First + Math.Max(0, Math.Ceiling((time - this.First) / this.Period)) * this.Period);
}

/// <summary>
/// Vblank times of the monitor that shows a window, from its scanline counter (D3DKMTGetScanLine) and its signal timing
/// (QueryDisplayConfig). DwmGetCompositionTimingInfo's qpcVBlank is not usable for this: it extrapolates with the
/// nominal refresh period and drifts against the scan-out. Thread safe.
/// </summary>
public sealed unsafe class ScanOut: IDisposable
{
	private const int ReadAttempts = 4096;

	[StructLayout(LayoutKind.Sequential)]
	private struct OpenAdapterFromGdiDisplayName
	{
		public fixed char DeviceName[32];
		public uint Adapter;
		public LUID AdapterLuid;
		public uint VidPnSourceId;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct GetScanLine
	{
		public uint Adapter;
		public uint VidPnSourceId;
		public byte InVerticalBlank;
		public uint ScanLine;
	}

	[DllImport("gdi32.dll")]
	private static extern int D3DKMTOpenAdapterFromGdiDisplayName(OpenAdapterFromGdiDisplayName* args);

	[DllImport("gdi32.dll")]
	private static extern int D3DKMTGetScanLine(GetScanLine* args);

	[DllImport("gdi32.dll")]
	private static extern int D3DKMTCloseAdapter(uint* adapter);

	private readonly uint source;
	private readonly uint totalLines;

	// The counter value where the vblank starts. Line 0 can lie inside the vblank.
	private readonly uint vblankLine;
	private uint adapter;

	private ScanOut(HMONITOR monitor, uint adapter, uint source, uint totalLines, uint vblankLine, double period)
	{
		this.Monitor = monitor;
		this.adapter = adapter;
		this.source = source;
		this.totalLines = totalLines;
		this.vblankLine = vblankLine;
		this.Period = period;
	}

	public HMONITOR Monitor { get; }

	/// <summary>
	/// The refresh period from the display configuration, in Stopwatch ticks. It can be the nominal one. Each vblank time
	/// comes from a fresh scanline read, so its error only scales the distance from that read.
	/// </summary>
	public double Period { get; }

	/// <summary>Returns null when the display does not report its scan-out. Blocks for up to three refreshes.</summary>
	public static ScanOut? Open(HWND window)
	{
		HMONITOR monitor = MonitorFromWindow(window, MONITOR.MONITOR_DEFAULTTONEAREST);
		MONITORINFOEXW info = default;
		info.Base.cbSize = (uint)sizeof(MONITORINFOEXW);
		if (monitor == HMONITOR.NULL || !GetMonitorInfoW(monitor, (MONITORINFO*)&info))
			return null;

		ReadOnlySpan<char> device = info.szDevice;
		if (!TryReadSignal(device, out DISPLAYCONFIG_VIDEO_SIGNAL_INFO signal))
			return null;

		OpenAdapterFromGdiDisplayName open = default;
		device.CopyTo(new Span<char>(open.DeviceName, 32));
		if (D3DKMTOpenAdapterFromGdiDisplayName(&open) != 0)
			return null;

		uint adapter = open.Adapter;
		double period = (double)Stopwatch.Frequency * signal.vSyncFreq.Denominator / signal.vSyncFreq.Numerator;
		if (!TryFindVBlankLine(adapter, open.VidPnSourceId, (long)(3 * period), out uint vblankLine) || vblankLine >= signal.totalSize.cy)
		{
			D3DKMTCloseAdapter(&adapter);
			return null;
		}

		return new ScanOut(monitor, adapter, open.VidPnSourceId, signal.totalSize.cy, vblankLine, period);
	}

	/// <summary>The first vblank start at or after time. Returns false when the scanline read fails.</summary>
	public bool TryNextVBlank(long time, out long vblank) => this.TryVBlank(time, Math.Ceiling, out vblank);

	/// <summary>The vblank start nearest to time. Returns false when the scanline read fails.</summary>
	public bool TryNearestVBlank(long time, out long vblank) => this.TryVBlank(time, Math.Round, out vblank);

	/// <summary>toPeriods picks the whole number of periods from the read vblank to time</summary>
	private bool TryVBlank(long time, Func<double, double> toPeriods, out long vblank)
	{
		if (!this.TryReadVBlank(out double start))
		{
			vblank = time;
			return false;
		}

		vblank = (long)Math.Ceiling(start + (toPeriods((time - start) / this.Period) * this.Period));
		return true;
	}

	/// <summary>
	/// The next vblank start, from one read outside the vblank. Some drivers do not count lines inside it, so a read
	/// there repeats.
	/// </summary>
	private bool TryReadVBlank(out double start)
	{
		double lineTicks = this.Period / this.totalLines;
		for (int i = 0; i < ReadAttempts; i++)
		{
			if (!TryRead(this.adapter, this.source, out bool vblank, out uint line))
				break;

			long now = Stopwatch.GetTimestamp();
			if (vblank)
				continue;

			start = now + ((this.vblankLine + this.totalLines - line) % this.totalLines * lineTicks);
			return true;
		}

		start = 0;
		return false;
	}

	/// <summary>The counter's value at the first read after the vblank flag rises</summary>
	private static bool TryFindVBlankLine(uint adapter, uint source, long timeout, out uint vblankLine)
	{
		long end = Stopwatch.GetTimestamp() + timeout;
		bool previous = true;
		while (Stopwatch.GetTimestamp() < end)
		{
			if (!TryRead(adapter, source, out bool vblank, out uint line))
				break;

			if (vblank && !previous)
			{
				vblankLine = line;
				return true;
			}

			previous = vblank;
		}

		vblankLine = 0;
		return false;
	}

	private static bool TryRead(uint adapter, uint source, out bool vblank, out uint line)
	{
		GetScanLine args = new() { Adapter = adapter, VidPnSourceId = source };
		bool read = D3DKMTGetScanLine(&args) == 0;
		vblank = args.InVerticalBlank != 0;
		line = args.ScanLine;
		return read;
	}

	private static bool TryReadSignal(ReadOnlySpan<char> device, out DISPLAYCONFIG_VIDEO_SIGNAL_INFO signal)
	{
		signal = default;
		uint pathCount, modeCount;
		if (GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, &pathCount, &modeCount) != Win32Error.ERROR_SUCCESS)
			return false;

		DISPLAYCONFIG_PATH_INFO[] paths = new DISPLAYCONFIG_PATH_INFO[pathCount];
		DISPLAYCONFIG_MODE_INFO[] modes = new DISPLAYCONFIG_MODE_INFO[modeCount];
		fixed (DISPLAYCONFIG_PATH_INFO* pathData = paths)
		fixed (DISPLAYCONFIG_MODE_INFO* modeData = modes)
		{
			if (QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, &pathCount, pathData, &modeCount, modeData, null) != Win32Error.ERROR_SUCCESS)
				return false;
		}

		device = TrimNull(device);
		for (int i = 0; i < pathCount; i++)
		{
			DISPLAYCONFIG_PATH_INFO path = paths[i];
			DISPLAYCONFIG_SOURCE_DEVICE_NAME name = default;
			name.header.type = DISPLAYCONFIG_DEVICE_INFO_TYPE.DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME;
			name.header.size = (uint)sizeof(DISPLAYCONFIG_SOURCE_DEVICE_NAME);
			name.header.adapterId = path.sourceInfo.adapterId;
			name.header.id = path.sourceInfo.id;
			if (DisplayConfigGetDeviceInfo(&name.header) != Win32Error.ERROR_SUCCESS)
				continue;

			if (!TrimNull(name.viewGdiDeviceName).SequenceEqual(device))
				continue;

			uint index = path.targetInfo.Anonymous.modeInfoIdx;
			if (index >= modeCount || modes[index].infoType != DISPLAYCONFIG_MODE_INFO_TYPE.DISPLAYCONFIG_MODE_INFO_TYPE_TARGET)
				return false;

			signal = modes[index].Anonymous.targetMode.targetVideoSignalInfo;
			return signal.vSyncFreq.Numerator != 0 && signal.totalSize.cy > signal.activeSize.cy;
		}

		return false;
	}

	private static ReadOnlySpan<char> TrimNull(ReadOnlySpan<char> text) => text.IndexOf('\0') is >= 0 and var end ? text[..end] : text;

	public void Dispose()
	{
		if (this.adapter == 0)
			return;

		uint handle = this.adapter;
		this.adapter = 0;
		D3DKMTCloseAdapter(&handle);
	}
}
