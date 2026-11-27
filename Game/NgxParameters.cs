using System;
using System.Buffers;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace UpscaleBuddy.Game;

/// <summary>
/// Replaces NVSDK_NGX_Parameter with a name/value store.
/// </summary>
public unsafe class NgxParameters: IDisposable
{
	public const int Success = 1;
	public const int Fail = unchecked((int)0xBAD00000);

	// 0 SetVoidPointer, 1 SetD3d12Resource, 2 SetD3d11Resource, 3 SetI, 4 SetUI, 5 SetD, 6 SetF, 7 SetULL,
	// 8 GetVoidPointer, 9 GetD3d12Resource, 10 GetD3d11Resource, 11 GetI, 12 GetUI, 13 GetD, 14 GetF, 15 GetULL, 16 Reset
	private const int SlotCount = 17;

	private const int NameBufferLength = 128; // A longer name takes the allocating path

	private delegate void SetPointerDelegate(nint self, nint name, nint value);
	private delegate void SetIntDelegate(nint self, nint name, int value);
	private delegate void SetUIntDelegate(nint self, nint name, uint value);
	private delegate void SetDoubleDelegate(nint self, nint name, double value);
	private delegate void SetFloatDelegate(nint self, nint name, float value);
	private delegate void SetULongDelegate(nint self, nint name, ulong value);
	private delegate int GetPointerDelegate(nint self, nint name, nint* value);
	private delegate int GetIntDelegate(nint self, nint name, int* value);
	private delegate int GetUIntDelegate(nint self, nint name, uint* value);
	private delegate int GetDoubleDelegate(nint self, nint name, double* value);
	private delegate int GetFloatDelegate(nint self, nint name, float* value);
	private delegate int GetULongDelegate(nint self, nint name, ulong* value);
	private delegate void ResetDelegate(nint self);

	private readonly Dictionary<string, double> numbers = new(StringComparer.Ordinal);
	private readonly Dictionary<string, nint> pointers = new(StringComparer.Ordinal);

	// The same dictionaries, keyed by chars on the stack. They make a string only for a new key.
	private readonly Dictionary<string, double>.AlternateLookup<ReadOnlySpan<char>> numbersByChars;
	private readonly Dictionary<string, nint>.AlternateLookup<ReadOnlySpan<char>> pointersByChars;
	private readonly object storeLock = new();
	private readonly List<Delegate> keepAlive = [];
	private readonly nint* vtable;

	public NgxParameters()
	{
		this.numbersByChars = this.numbers.GetAlternateLookup<ReadOnlySpan<char>>();
		this.pointersByChars = this.pointers.GetAlternateLookup<ReadOnlySpan<char>>();

		// The game reaches it only through the DLSS object (+0x158). The game reads that field at each use and never
		// copies it. DlssPath restores the DLSS object before it disposes this.
		this.Address = Marshal.AllocHGlobal(8 + (SlotCount * 8));
		this.vtable = (nint*)(this.Address + 8);
		*(nint*)this.Address = (nint)this.vtable;

		this.vtable[0] = this.Callback<SetPointerDelegate>((_, n, v) => this.SetPointer(n, v));
		this.vtable[1] = this.Callback<SetPointerDelegate>((_, n, v) => this.SetPointer(n, v));
		this.vtable[2] = this.Callback<SetPointerDelegate>((_, n, v) => this.SetPointer(n, v));
		this.vtable[3] = this.Callback<SetIntDelegate>((_, n, v) => this.SetNumber(n, v));
		this.vtable[4] = this.Callback<SetUIntDelegate>((_, n, v) => this.SetNumber(n, v));
		this.vtable[5] = this.Callback<SetDoubleDelegate>((_, n, v) => this.SetNumber(n, v));
		this.vtable[6] = this.Callback<SetFloatDelegate>((_, n, v) => this.SetNumber(n, v));
		this.vtable[7] = this.Callback<SetULongDelegate>((_, n, v) => this.SetNumber(n, v));
		this.vtable[8] = this.Callback<GetPointerDelegate>((_, n, v) => this.TryPointer(n, out nint p) ? Write(v, p) : Fail);
		this.vtable[9] = this.Callback<GetPointerDelegate>((_, n, v) => this.TryPointer(n, out nint p) ? Write(v, p) : Fail);
		this.vtable[10] = this.Callback<GetPointerDelegate>((_, n, v) => this.TryPointer(n, out nint p) ? Write(v, p) : Fail);
		this.vtable[11] = this.Callback<GetIntDelegate>((_, n, v) => this.TryNumber(n, out double x) ? Write(v, (int)x) : Fail);
		this.vtable[12] = this.Callback<GetUIntDelegate>((_, n, v) => this.TryNumber(n, out double x) ? Write(v, (uint)x) : Fail);
		this.vtable[13] = this.Callback<GetDoubleDelegate>((_, n, v) => this.TryNumber(n, out double x) ? Write(v, x) : Fail);
		this.vtable[14] = this.Callback<GetFloatDelegate>((_, n, v) => this.TryNumber(n, out double x) ? Write(v, (float)x) : Fail);
		this.vtable[15] = this.Callback<GetULongDelegate>((_, n, v) => this.TryNumber(n, out double x) ? Write(v, (ulong)x) : Fail);
		this.vtable[16] = this.Callback<ResetDelegate>(_ => { });
	}

	/// <summary>NVSDK_NGX_Parameter* given to the game</summary>
	public nint Address { get; }

	public double Number(string name)
	{
		lock (this.storeLock)
			return this.numbers.GetValueOrDefault(name);
	}

	public nint Pointer(string name)
	{
		lock (this.storeLock)
			return this.pointers.GetValueOrDefault(name);
	}

	public void SetNumber(string name, double value)
	{
		lock (this.storeLock)
			this.numbers[name] = value;
	}

	public void SetPointer(string name, nint value)
	{
		lock (this.storeLock)
			this.pointers[name] = value;
	}

	/// <summary>Returns a function pointer for a callback. This object keeps the callback alive.</summary>
	public nint Callback<T>(T callback) where T : Delegate
	{
		this.keepAlive.Add(callback);
		return Marshal.GetFunctionPointerForDelegate(callback);
	}

	private void SetNumber(nint name, double value)
	{
#if DEBUG
		using PerfLog.Scope perf = PerfLog.Measure(PerfLog.Probe.NgxParameter);
#endif
		ReadOnlySpan<char> key = Name(name, stackalloc char[NameBufferLength]);
		lock (this.storeLock)
			this.numbersByChars[key] = value;
	}

	private void SetPointer(nint name, nint value)
	{
#if DEBUG
		using PerfLog.Scope perf = PerfLog.Measure(PerfLog.Probe.NgxParameter);
#endif
		ReadOnlySpan<char> key = Name(name, stackalloc char[NameBufferLength]);
		lock (this.storeLock)
			this.pointersByChars[key] = value;
	}

	private bool TryNumber(nint name, out double value)
	{
#if DEBUG
		using PerfLog.Scope perf = PerfLog.Measure(PerfLog.Probe.NgxParameter);
#endif
		ReadOnlySpan<char> key = Name(name, stackalloc char[NameBufferLength]);
		lock (this.storeLock)
			return this.numbersByChars.TryGetValue(key, out value);
	}

	private bool TryPointer(nint name, out nint value)
	{
#if DEBUG
		using PerfLog.Scope perf = PerfLog.Measure(PerfLog.Probe.NgxParameter);
#endif
		ReadOnlySpan<char> key = Name(name, stackalloc char[NameBufferLength]);
		lock (this.storeLock)
			return this.pointersByChars.TryGetValue(key, out value);
	}

	/// <summary>
	/// The characters of a null-terminated name. An ASCII name that fits goes into buffer, without an allocation.
	/// Other names become a string in the ANSI code page.
	/// </summary>
	private static ReadOnlySpan<char> Name(nint name, Span<char> buffer)
	{
		ReadOnlySpan<byte> bytes = MemoryMarshal.CreateReadOnlySpanFromNullTerminated((byte*)name);
		if (Ascii.ToUtf16(bytes, buffer, out int length) == OperationStatus.Done)
			return buffer[..length];

		return Marshal.PtrToStringAnsi(name);
	}

	private static int Write<T>(T* target, T value) where T : unmanaged
	{
		*target = value;
		return Success;
	}

	public void Dispose()
	{
		Marshal.FreeHGlobal(this.Address);
		this.keepAlive.Clear();
		GC.SuppressFinalize(this);
	}
}
