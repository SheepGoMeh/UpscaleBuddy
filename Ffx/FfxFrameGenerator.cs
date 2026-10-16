using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading;

using TerraFX.Interop.DirectX;

using UpscaleBuddy.D3D12;
using UpscaleBuddy.FrameGeneration;
using UpscaleBuddy.Fsr3;

using static TerraFX.Interop.DirectX.DXGI_FORMAT;
using static UpscaleBuddy.Ffx.FfxApi;

namespace UpscaleBuddy.Ffx;

/// <summary>
/// AMD's frame generation DLL (FidelityFX SDK 2.x) over the bridge: FSR 4 frame generation where the provider supports it
/// (RX 9000), FSR 3.1 elsewhere. Without AMD's swapchain (FFX_FRAMEGENERATION_FLAG_NO_SWAPCHAIN_CONTEXT_NOTIFY): configure,
/// prepare and dispatch each frame, the generated frame into our own output
/// </summary>
public unsafe class FfxFrameGenerator: IFrameGenerator
{
	private const string FrameGenerationDll = "amd_fidelityfx_framegeneration_dx12.dll";

	private const ulong DescCreateFrameGeneration = 0x00020001;
	private const ulong DescConfigureFrameGeneration = 0x00020002;
	private const ulong DescDispatchFrameGeneration = 0x00020003;
	private const ulong DescDispatchPrepareV2 = 0x0002000C;
	private const ulong DescCreateFrameGenerationVersion = 0x0002000E;
	private const uint FrameGenerationVersion = (4 << 22) | (0 << 12) | 1; // FFX_FRAMEGENERATION_VERSION 4.0.1, SDK 2.3

	private const uint CreateDepthInverted = 1 << 3;
	private const uint CreateDepthInfinite = 1 << 4;
	private const uint DispatchNoSwapchain = 1 << 3;
	private const uint TransferFunctionSrgb = 0;

	[StructLayout(LayoutKind.Sequential)]
	private struct Dimensions
	{
		public uint Width, Height;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct Rect
	{
		public int Left, Top, Width, Height;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct CreateFrameGeneration
	{
		public Header Header;
		public uint Flags;
		public Dimensions DisplaySize;
		public Dimensions MaxRenderSize;
		public uint BackBufferFormat;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct CreateFrameGenerationVersion
	{
		public Header Header;
		public uint Version;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct ConfigureFrameGeneration
	{
		public Header Header;
		public void* SwapChain;
		public nint PresentCallback, PresentCallbackUserContext;
		public nint FrameGenerationCallback, FrameGenerationCallbackUserContext;
		public byte FrameGenerationEnabled;
		public byte AllowAsyncWorkloads;
		public Resource HudLessColor;
		public uint Flags;
		public byte OnlyPresentGenerated;
		public Rect GenerationRect;
		public ulong FrameId;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct DispatchPrepareV2
	{
		public Header Header;
		public ulong FrameId;
		public uint Flags;
		public ID3D12GraphicsCommandList* CommandList;
		public Dimensions RenderSize;
		public float JitterX, JitterY;
		public float MotionVectorScaleX, MotionVectorScaleY;
		public float FrameTimeDelta;
		public byte Reset;
		public float CameraNear, CameraFar, CameraFovAngleVertical, ViewSpaceToMetersFactor;
		public Resource Depth;
		public Resource MotionVectors;
		public Vector3 CameraPosition, CameraUp, CameraRight, CameraForward;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct DispatchFrameGeneration
	{
		public Header Header;
		public ID3D12GraphicsCommandList* CommandList;
		public Resource PresentColor;
		public Resource Output0, Output1, Output2, Output3;
		public uint NumGeneratedFrames;
		public byte Reset;
		public uint BackbufferTransferFunction;
		public float MinLuminance, MaxLuminance;
		public Rect GenerationRect;
		public ulong FrameId;
	}


	private const int SlotCount = 4;
	private const uint JoinMs = 2000;

	// Frames dropped for longer than this have left the generator's history too old to follow; shorter gaps (the GPU held up for a
	// moment) keep it
	private const double StaleSeconds = 2;

	/// <summary>One frame in flight: its own copies of the inputs and its output, reused every <see cref="SlotCount"/> frames</summary>
	private sealed class Slot
	{
		public Twin Scene = null!, Output = null!, Depth = null!, Motion = null!;
		public ID3D11ShaderResourceView* OutputView;
		public readonly ManualResetEventSlim Submitted = new(true);

		// Guarded by the generator's gate
		public long Serial;
		public ulong Completion;
		public bool Failed;
		public int Readers;
		public long SubmitStamp;
	}

	private sealed record Job(Slot Slot, long Serial, ulong Input, FrameInputs Frame, ulong FrameId, bool Reset);

	private readonly Bridge bridge;
	private readonly object gate = new();
	private readonly BlockingCollection<Job> jobs = [];
	private readonly Thread worker;

	// D3D11 marks: start, inputs copied
	private readonly GpuTimer timer;
	private readonly uint width, height;
	private nint context;
	private Slot[]? slots;
	private int next;
	private long serial;
	private ulong frameId;
	private bool first = true;
	private bool resetNext;
	private long droppingSince;
	private bool disposed;
	private Exception? workerFailure;
	private long dropped;
	private double submitMs, readyMs;

	public FfxFrameGenerator(ID3D11Device* device, uint width, uint height, uint maxRenderWidth, uint maxRenderHeight, bool infiniteFar)
	{
		this.width = width;
		this.height = height;
		Load(FrameGenerationDll);
		this.bridge = new Bridge(device);
		try
		{
			this.timer = new GpuTimer(device, 2);
			BackendDx12 backend = new() { Header = { Type = DescBackendDx12 }, Device = this.bridge.Device12 };
			CreateFrameGenerationVersion version = new()
			{
				Header = { Type = DescCreateFrameGenerationVersion, Next = &backend.Header }, Version = FrameGenerationVersion,
			};
			CreateFrameGeneration create = new()
			{
				Header = { Type = DescCreateFrameGeneration, Next = &version.Header },
				// Same as the upscaler: LDR, render size motion vectors without jitter, inverted depth
				Flags = CreateDepthInverted | (infiniteFar ? CreateDepthInfinite : 0),
				DisplaySize = new Dimensions { Width = width, Height = height },
				MaxRenderSize = new Dimensions { Width = maxRenderWidth, Height = maxRenderHeight },
				BackBufferFormat = Format(DXGI_FORMAT_B8G8R8A8_UNORM),
			};
			this.context = CreateContext(&create.Header, "ffxCreateContext (frame generation)");

			string? provider = ProviderName(this.context);
			this.Name = provider != null ? $"FSR {provider} frame generation (AMD DLL, D3D12)" : "FSR frame generation (AMD DLL, D3D12)";
			this.worker = new Thread(this.Work) { Name = "UpscaleBuddy frame generation", IsBackground = true };
			this.worker.Start();
		}
		catch
		{
			this.Dispose();
			throw;
		}
	}

	public string Name { get; } = "FSR frame generation (AMD DLL, D3D12)";

	/// <summary>Copy in is the game's queue; library is the D3D12 queue's own time, ready is submit to finished as the presenter saw it</summary>
	public string Timings
	{
		get
		{
			double copyMs = this.timer.Ms[0];
			return $"GPU on the game's queue {copyMs:F3} ms (copy in), D3D12 queue {this.bridge.RecordedMs:F3} ms, ready {this.readyMs:F2} ms " +
			       $"after submit, render thread submit {this.submitMs:F3} ms, CPU stall on allocators {this.bridge.CpuStallMs:F3} ms, " +
			       $"{Interlocked.Read(ref this.dropped)} dropped";
		}
	}

	public GenerationTicket? Submit(ID3D11DeviceContext* d3d11, in FrameInputs inputs)
	{
		if (this.workerFailure != null)
			throw new InvalidOperationException("Frame generation worker failed", this.workerFailure);

		long start = Stopwatch.GetTimestamp();
		this.slots ??= this.CreateSlots(d3d11, inputs);
		Slot slot = this.slots[this.next];
		long ticketSerial;
		lock (this.gate)
		{
			if (!this.IsFree(slot))
			{
				long now = Stopwatch.GetTimestamp();
				if (this.droppingSince == 0)
					this.droppingSince = now;
				else if (Stopwatch.GetElapsedTime(this.droppingSince, now).TotalSeconds >= StaleSeconds && !this.resetNext)
				{
					Service.PluginLog.Warning($"Frame generation has been behind for {StaleSeconds} s, the next frame starts its history over");
					this.resetNext = true;
				}

				Interlocked.Increment(ref this.dropped);
				return null;
			}

			this.droppingSince = 0;
			slot.Submitted.Reset();
			slot.Failed = false;
			slot.Completion = 0;
			slot.SubmitStamp = start;
			slot.Serial = ticketSerial = ++this.serial;
		}

		int index = this.next;
		this.next = (this.next + 1) % SlotCount;
		if (inputs.Measure)
			this.timer.Begin(d3d11);

		// Always copies: the game reuses its textures while the D3D12 queue still reads these
		Bridge.Copy(d3d11, slot.Scene.Texture11, inputs.Scene, slot.Scene.Input);
		Bridge.Copy(d3d11, slot.Motion.Texture11, inputs.MotionVectors, slot.Motion.Input);
		this.bridge.CopyDepth(d3d11, inputs.Depth, slot.Depth, inputs.RenderWidth, inputs.RenderHeight);
		if (inputs.Measure)
		{
			this.timer.Mark(d3d11, 1);
			this.timer.End(d3d11);
		}

		ulong input = this.bridge.SignalInput(d3d11);
		bool reset = inputs.Reset || this.first || this.resetNext;
		this.first = false;
		this.resetNext = false;
		this.jobs.Add(new Job(slot, ticketSerial, input, inputs, ++this.frameId, reset));
		this.submitMs = (this.submitMs * 0.9) + (Stopwatch.GetElapsedTime(start).TotalMilliseconds * 0.1);
		return new GenerationTicket(this, index, ticketSerial);
	}

	public bool Wait(GenerationTicket ticket, uint timeoutMs)
	{
		if (this.slots == null)
			return false;

		Slot slot = this.slots[ticket.Slot];
		long deadline = Stopwatch.GetTimestamp() + (timeoutMs * Stopwatch.Frequency / 1000);
		if (Volatile.Read(ref slot.Serial) != ticket.Serial || !slot.Submitted.Wait((int)timeoutMs))
			return false;

		ulong completion;
		lock (this.gate)
		{
			if (slot.Serial != ticket.Serial || slot.Failed)
				return false;
			completion = slot.Completion;
		}

		long remaining = (deadline - Stopwatch.GetTimestamp()) * 1000 / Stopwatch.Frequency;
		if (!this.bridge.WaitCompleted(completion, (uint)Math.Max(remaining, 0)))
			return false;

		this.readyMs = (this.readyMs * 0.9) + (Stopwatch.GetElapsedTime(slot.SubmitStamp).TotalMilliseconds * 0.1);
		return true;
	}

	public bool TryAcquire(GenerationTicket ticket, ID3D11DeviceContext* d3d11)
	{
		Slot? slot = this.slots?[ticket.Slot];
		if (slot == null)
			return false;

		lock (this.gate)
		{
			if (this.disposed || slot.Serial != ticket.Serial || slot.Failed || !this.bridge.IsComplete(slot.Completion))
				return false;
			slot.Readers++;
		}

		this.bridge.WaitOnGpu(d3d11, slot.Completion);
		return true;
	}

	public ID3D11ShaderResourceView* View(GenerationTicket ticket) => this.slots![ticket.Slot].OutputView;

	public ID3D11Texture2D* Output(GenerationTicket ticket) => this.slots![ticket.Slot].Output.Texture11;

	public void Release(GenerationTicket ticket)
	{
		lock (this.gate)
			this.slots![ticket.Slot].Readers--;
	}

	/// <summary>Called with the gate held</summary>
	private bool IsFree(Slot slot) =>
		slot.Serial == 0 || (slot.Readers == 0 && slot.Submitted.IsSet && (slot.Failed || this.bridge.IsComplete(slot.Completion)));

	private Slot[] CreateSlots(ID3D11DeviceContext* d3d11, in FrameInputs inputs)
	{
		D3D11_TEXTURE2D_DESC depthDesc = Dx.GetDesc(inputs.Depth);
		D3D11_TEXTURE2D_DESC motionDesc = Dx.GetDesc(inputs.MotionVectors);
		ID3D11Device* device;
		d3d11->GetDevice(&device);
		Slot[] created = new Slot[SlotCount];
		try
		{
			for (int i = 0; i < SlotCount; i++)
			{
				Slot slot = created[i] = new Slot();
				slot.Scene = this.bridge.CreateTwin(DXGI_FORMAT_B8G8R8A8_UNORM, this.width, this.height);
				slot.Output = this.bridge.CreateTwin(DXGI_FORMAT_B8G8R8A8_UNORM, this.width, this.height);
				slot.Depth = this.bridge.CreateTwin(DXGI_FORMAT_R32_FLOAT, depthDesc.Width, depthDesc.Height);
				slot.Motion = this.bridge.CreateTwin(motionDesc.Format, motionDesc.Width, motionDesc.Height);
				slot.OutputView = Dx.CreateSrv(device, slot.Output.Texture11);
			}
		}
		catch
		{
			foreach (Slot? slot in created)
			{
				if (slot != null)
					FreeSlot(slot);
			}

			throw;
		}
		finally
		{
			device->Release();
		}

		return created;
	}

	private static void FreeSlot(Slot slot)
	{
		Dx.Release(slot.OutputView);
		Bridge.Release(slot.Scene);
		Bridge.Release(slot.Output);
		Bridge.Release(slot.Depth);
		Bridge.Release(slot.Motion);
		slot.Submitted.Dispose();
	}

	/// <summary>The worker thread: records and queues each frame's D3D12 work behind its input fence</summary>
	private void Work()
	{
		foreach (Job job in this.jobs.GetConsumingEnumerable())
		{
			try
			{
				ulong completion = this.bridge.SubmitAsync(job.Input, list => this.Record(list, job), job.Frame.Measure);
				lock (this.gate)
					job.Slot.Completion = completion;
			}
			catch (Exception e)
			{
				this.workerFailure = e;
				lock (this.gate)
					job.Slot.Failed = true;
			}
			finally
			{
				job.Slot.Submitted.Set();
			}
		}
	}

	private void Record(ID3D12GraphicsCommandList* list, Job job)
	{
		FrameInputs frame = job.Frame;
		Slot slot = job.Slot;
		Rect rect = new() { Width = (int)this.width, Height = (int)this.height };
		byte reset = job.Reset ? (byte)1 : (byte)0;

		ConfigureFrameGeneration configure = new()
		{
			Header = { Type = DescConfigureFrameGeneration },
			FrameGenerationEnabled = 1,
			Flags = DispatchNoSwapchain,
			GenerationRect = rect,
			FrameId = job.FrameId,
		};
		Configure(this.context, &configure.Header, "ffxConfigure (frame generation)");

		DispatchPrepareV2 prepare = new()
		{
			Header = { Type = DescDispatchPrepareV2 },
			FrameId = job.FrameId,
			Flags = DispatchNoSwapchain,
			CommandList = list,
			RenderSize = new Dimensions { Width = frame.RenderWidth, Height = frame.RenderHeight },
			JitterX = frame.JitterX,
			JitterY = frame.JitterY,
			MotionVectorScaleX = frame.MotionVectorScaleX,
			MotionVectorScaleY = frame.MotionVectorScaleY,
			FrameTimeDelta = frame.FrameTimeMs,
			Reset = reset,
			CameraNear = frame.CameraNear,
			CameraFar = frame.CameraFar,
			CameraFovAngleVertical = frame.CameraFovY,
			ViewSpaceToMetersFactor = 1.0f,
			Depth = Describe(slot.Depth.Input),
			MotionVectors = Describe(slot.Motion.Input),
			CameraPosition = frame.CameraPosition,
			CameraUp = frame.CameraUp,
			CameraRight = frame.CameraRight,
			CameraForward = frame.CameraForward,
		};
		Dispatch(this.context, &prepare.Header, "ffxDispatch (frame generation prepare)");

		DispatchFrameGeneration generate = new()
		{
			Header = { Type = DescDispatchFrameGeneration },
			CommandList = list,
			PresentColor = Describe(slot.Scene.Input),
			Output0 = Describe(slot.Output.Input),
			NumGeneratedFrames = 1,
			Reset = reset,
			BackbufferTransferFunction = TransferFunctionSrgb,
			GenerationRect = rect,
			FrameId = job.FrameId,
		};
		Dispatch(this.context, &generate.Header, "ffxDispatch (frame generation)");
	}

	public void Dispose()
	{
		lock (this.gate)
			this.disposed = true;

		this.jobs.CompleteAdding();
		this.worker?.Join((int)JoinMs);

		// A frame being read finishes first
		for (int i = 0; i < JoinMs && this.slots != null; i++)
		{
			lock (this.gate)
			{
				if (this.slots.All(slot => slot.Readers == 0))
					break;
			}

			Thread.Sleep(1);
		}

		this.bridge?.WaitIdle();
		Destroy(ref this.context);
		if (this.slots != null)
		{
			foreach (Slot slot in this.slots)
				FreeSlot(slot);
			this.slots = null;
		}

		this.timer?.Dispose();
		this.bridge?.Dispose();
		this.jobs.Dispose();
		GC.SuppressFinalize(this);
	}
}
