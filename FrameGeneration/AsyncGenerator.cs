using System;
using System.Collections.Concurrent;
using System.Threading;

using TerraFX.Interop.DirectX;

using UpscaleBuddy.D3D12;
using UpscaleBuddy.Ffx;
using UpscaleBuddy.Game;

using static TerraFX.Interop.DirectX.DXGI_FORMAT;
using static TerraFX.Interop.Windows.Windows;

namespace UpscaleBuddy.FrameGeneration;

/// <summary>
/// This class never makes D3D11 wait. The render thread calls Submit. The presenter thread calls Wait, Finished and
/// Release. A slot is free after Release and after its output fence value completes.
/// </summary>
public sealed unsafe class AsyncGenerator: IDisposable
{
	private const int Slots = 4;

	private sealed class Slot(int index)
	{
		public readonly int Index = index;
		public Twin? Scene, Depth, MotionVectors, Ui, Generated, Finished;
		public ulong Output; // The output fence value of the last Submit for this slot
		public volatile bool Busy; // True from Submit until Release
		public volatile bool Failed; // True when the last recording for this slot threw
	}

	private readonly record struct Job(Slot Slot, FrameInputs Inputs, ulong Input, ulong Output, bool Reset, ulong FrameId, FinishConstants Finish,
		int LutIndex);

	/// <summary>What the worker's record callback needs for one job</summary>
	private struct RecordState
	{
		public AsyncGenerator Owner;
		public Job Job;
		public GenerationResources Resources;
		public FinishResources FinishResources;
	}

	private readonly Bridge bridge;
	private readonly FinishCalibrationResult calibration;
	private readonly ColorFilterLuts luts;
	private readonly IFrameGenerator generator;
	private readonly FinishPass finish;
	private readonly uint width, height;
	private readonly Slot[] slots = new Slot[Slots];
	private readonly BlockingCollection<Job> jobs = new();
	private readonly Thread worker;
	private int next;
	private ulong frameId;
	private bool resetNext = true;
	private volatile Exception? failure;

	/// <summary>The caller owns bridge, calibration and luts. They must outlive this generator.</summary>
	public AsyncGenerator(Bridge bridge, FinishCalibrationResult calibration, ColorFilterLuts luts, uint width, uint height, uint maxRenderWidth,
		uint maxRenderHeight, bool infiniteFar)
	{
		this.bridge = bridge;
		this.calibration = calibration;
		this.luts = luts;
		this.width = width;
		this.height = height;
		for (int i = 0; i < Slots; i++)
			this.slots[i] = new Slot(i);
		this.finish = new FinishPass(bridge.Device12, Slots);
		try
		{
			this.generator = new FfxFrameGenerator(bridge, width, height, maxRenderWidth, maxRenderHeight, infiniteFar);
		}
		catch
		{
			this.finish.Dispose();
			throw;
		}

		this.worker = new Thread(this.Work) { Name = "UpscaleBuddy frame generation", IsBackground = true, Priority = ThreadPriority.AboveNormal };
		this.worker.Start();
	}

	public string Name => this.generator.Name;

	/// <summary>When this is true, Submit returns null.</summary>
	public bool Failed => this.failure != null;

	public Exception? Failure => this.failure;

	/// <summary>When this is true, every fence value reads as complete.</summary>
	public bool DeviceLost => this.bridge.DeviceLost;

	/// <summary>Call on the render thread. Returns null when no slot is free or when Failed is true.</summary>
	public GenerationTicket? Submit(ID3D11DeviceContext* context, in FrameInputs inputs, ID3D11Texture2D* ui, bool uiDrawn, in FinishConstants finish,
		int lutIndex)
	{
		Slot? slot = this.FreeSlot();
		if (slot == null || this.Failed)
		{
			this.resetNext = true;
			return null;
		}

		slot.Scene ??= this.bridge.CreateTwin(DXGI_FORMAT_B8G8R8A8_UNORM, this.width, this.height);
		slot.Generated ??= this.bridge.CreateTwin(DXGI_FORMAT_B8G8R8A8_UNORM, this.width, this.height);
		slot.Finished ??= this.bridge.CreateTwin(DXGI_FORMAT_R8G8B8A8_TYPELESS, this.width, this.height);
		slot.Depth ??= this.bridge.CreateTwinLike(inputs.Depth, DXGI_FORMAT_R32_FLOAT);
		slot.MotionVectors ??= this.bridge.CreateTwinLike(inputs.MotionVectors);
		slot.Ui ??= this.bridge.CreateTwinLike(ui);

		Bridge.Copy(context, slot.Scene.Texture11, inputs.Scene, slot.Scene.Input);
		Bridge.Copy(context, slot.MotionVectors.Texture11, inputs.MotionVectors, slot.MotionVectors.Input);
		this.bridge.CopyDepth(context, inputs.Depth, slot.Depth, inputs.RenderWidth, inputs.RenderHeight);
		if (uiDrawn)
			Bridge.Copy(context, slot.Ui.Texture11, ui, slot.Ui.Input);

		ulong input = this.bridge.SignalInput(context);
		ulong output = this.bridge.ReserveOutput();
		FinishConstants constants = finish;
		if (uiDrawn)
			constants.Flags |= FinishConstants.UiDrawn;
		slot.Output = output;
		slot.Failed = false;
		slot.Busy = true;
		this.next = (slot.Index + 1) % Slots;
		this.jobs.Add(new Job(slot, inputs, input, output, inputs.Reset || this.resetNext, ++this.frameId, constants, lutIndex));
		this.resetNext = false;
		return new GenerationTicket(this, output, slot.Index);
	}

	/// <summary>Returns true also when DeviceLost is true.</summary>
	public bool Ready(GenerationTicket ticket) => this.bridge.Completed(ticket.Output);

	/// <summary>Call after Ready returns true.</summary>
	public bool Usable(GenerationTicket ticket) => !this.slots[ticket.Slot].Failed && !this.DeviceLost;

	/// <summary>Call on the presenter thread.</summary>
	public bool Wait(GenerationTicket ticket, uint milliseconds) => this.bridge.WaitOutput(ticket.Output, milliseconds);

	/// <summary>The texture is R8G8B8A8_TYPELESS. It stays valid until Release.</summary>
	public ID3D11Texture2D* Finished(GenerationTicket ticket) => this.slots[ticket.Slot].Finished!.Texture11;

	public void Release(GenerationTicket ticket) => this.slots[ticket.Slot].Busy = false;

	/// <summary>The value is a running average.</summary>
	public double RecordedMs => this.bridge.RecordedMs;

	private Slot? FreeSlot()
	{
		for (int i = 0; i < Slots; i++)
		{
			Slot slot = this.slots[(this.next + i) % Slots];
			if (!slot.Busy && this.bridge.Completed(slot.Output))
				return slot;
		}

		return null;
	}

	private void Work()
	{
		foreach (Job job in this.jobs.GetConsumingEnumerable())
		{
#if DEBUG
			PerfLog.WorkerJob();
			using PerfLog.Scope perf = PerfLog.Measure(PerfLog.Probe.FrameGenerationWorker);
#endif
			try
			{
				if (this.failure != null)
				{
					// Every reserved output value needs a SubmitAsync.
					job.Slot.Failed = true;
					this.bridge.SubmitAsync(job.Input, job.Output, 0, static (ID3D12GraphicsCommandList* _, in int _) => { });
					continue;
				}

				Slot slot = job.Slot;
				RecordState state = new()
				{
					Owner = this, Job = job,
					Resources = new GenerationResources(slot.Scene!.Input, slot.Depth!.Input, slot.MotionVectors!.Input, slot.Generated!.Input),
					FinishResources = new FinishResources(slot.Generated.Input.Resource,
						(job.Finish.Flags & FinishConstants.UiDrawn) != 0 ? slot.Ui!.Input.Resource : null,
						job.Finish.Strength > 0 ? this.luts.For(job.LutIndex) : null, this.calibration.BlendLut, slot.Finished!.Input.Resource),
				};
				this.bridge.SubmitAsync(job.Input, job.Output, state, static (ID3D12GraphicsCommandList* list, in RecordState s) =>
				{
					try
					{
						s.Owner.generator.Record(list, s.Job.Inputs, s.Resources, s.Job.Reset, s.Job.FrameId);
						D3D12_RESOURCE_BARRIER barrier = D3D12_RESOURCE_BARRIER.InitUAV(null);
						list->ResourceBarrier(1, &barrier);
						s.Owner.finish.Record(list, s.Job.Slot.Index, s.FinishResources, s.Job.Finish);
					}
					catch (Exception e)
					{
						// Set Failed before SubmitAsync signals the output.
						s.Job.Slot.Failed = true;
						s.Owner.failure ??= e;
						throw;
					}
				}, true);
			}
			catch (Exception e)
			{
				this.failure ??= e;
			}
		}
	}

	/// <summary>
	/// Call after the last Submit. Blocks until Release for every ticket, for a maximum of 1 s.
	/// </summary>
	public void Dispose()
	{
		bool released = SpinWait.SpinUntil(() => Array.TrueForAll(this.slots, slot => !slot.Busy), 1000);
		this.jobs.CompleteAdding();
		this.worker.Join();
		this.jobs.Dispose();
		this.bridge.WaitIdle();
		this.generator.Dispose();
		this.finish.Dispose();
		foreach (Slot slot in this.slots)
		{
			// The presenter can still read the textures of a Busy slot.
			if (!released && slot.Busy)
				continue;

			Bridge.Release(slot.Scene);
			Bridge.Release(slot.Depth);
			Bridge.Release(slot.MotionVectors);
			Bridge.Release(slot.Ui);
			Bridge.Release(slot.Generated);
			Bridge.Release(slot.Finished);
		}
	}
}
