using System;
using System.Numerics;

using TerraFX.Interop.DirectX;

namespace UpscaleBuddy.FrameGeneration;

/// <summary>
/// One frame's inputs, after the frame's ToneAdjust: the UI-free scene at display size, and the upscaler's depth, motion
/// vectors and camera of the same frame
/// </summary>
public unsafe struct FrameInputs
{
	public ID3D11Texture2D* Scene;
	public ID3D11Texture2D* Depth;
	public ID3D11Texture2D* MotionVectors;
	public uint RenderWidth, RenderHeight;
	public float JitterX, JitterY;
	public float MotionVectorScaleX, MotionVectorScaleY;
	public float FrameTimeMs;
	public float CameraNear, CameraFar, CameraFovY;
	public bool InfiniteFar;
	public bool Reset;

	// Time the generator's GPU work on the game's queue
	public bool Measure;

	// World space; FSR 4 frame generation needs them, FSR 3.1 uses them from 3.1.4
	public Vector3 CameraPosition, CameraUp, CameraRight, CameraForward;
}

/// <summary>
/// A frame being generated, handed out by <see cref="IFrameGenerator.Submit"/>: wait for it, then read it (a generator isn't
/// disposed while it is read). Its slot is reused a few frames later, so a ticket that old is stale and waiting for it fails
/// </summary>
public sealed unsafe class GenerationTicket
{
	private readonly IFrameGenerator owner;

	internal GenerationTicket(IFrameGenerator owner, int slot, long serial)
	{
		this.owner = owner;
		this.Slot = slot;
		this.Serial = serial;
	}

	/// <summary>The generator's slot of this frame</summary>
	public int Slot { get; }

	/// <summary>Tells this frame from the one that has the slot next</summary>
	public long Serial { get; }

	/// <summary>Blocks until the frame is generated; false after timeoutMs, when generating failed or when the ticket is stale</summary>
	public bool Wait(uint timeoutMs) => this.owner.Wait(this, timeoutMs);

	/// <summary>
	/// After <see cref="Wait"/>: the frame (scene size, B8G8R8A8) can be read from <see cref="View"/> on the context until
	/// <see cref="Release"/>; false when the generator is gone. The context's later commands wait for it on the GPU, already met
	/// </summary>
	public bool TryAcquire(ID3D11DeviceContext* context) => this.owner.TryAcquire(this, context);

	/// <summary>Valid between <see cref="TryAcquire"/> and <see cref="Release"/></summary>
	public ID3D11ShaderResourceView* View => this.owner.View(this);

	/// <summary>Valid between <see cref="TryAcquire"/> and <see cref="Release"/></summary>
	public ID3D11Texture2D* Output => this.owner.Output(this);

	public void Release() => this.owner.Release(this);
}

/// <summary>A frame generation library: frames between the previous call's scene and this one</summary>
public unsafe interface IFrameGenerator: IDisposable
{
	/// <summary>Status name, with the library's version</summary>
	string Name { get; }

	/// <summary>Where the time goes, smoothed over frames where <see cref="FrameInputs.Measure"/> was set</summary>
	string Timings { get; }

	/// <summary>
	/// Render thread: the inputs are copied into a slot of the generator's own and the frame halfway between the previous call's
	/// scene and this one is queued on the generator's own queue and thread; nothing on the game's queue waits for it. Null when
	/// every slot is still in use (the next frame starts its history over) or the generator failed
	/// </summary>
	GenerationTicket? Submit(ID3D11DeviceContext* context, in FrameInputs inputs);

	bool Wait(GenerationTicket ticket, uint timeoutMs);

	bool TryAcquire(GenerationTicket ticket, ID3D11DeviceContext* context);

	ID3D11ShaderResourceView* View(GenerationTicket ticket);

	ID3D11Texture2D* Output(GenerationTicket ticket);

	void Release(GenerationTicket ticket);
}
