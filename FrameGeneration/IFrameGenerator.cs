using System;
using System.Numerics;

using TerraFX.Interop.DirectX;

using UpscaleBuddy.D3D12;

namespace UpscaleBuddy.FrameGeneration;

/// <summary>
/// One frame's inputs, after the frame's ToneAdjust:
/// - the UI-free scene at display size
/// - the upscaler's depth, motion vectors and camera of the same frame
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

	// World space. FSR 4 frame generation needs them. FSR 3.1 uses them from 3.1.4.
	public Vector3 CameraPosition, CameraUp, CameraRight, CameraForward;
}

/// <summary>All textures are in COMMON state.</summary>
public readonly struct GenerationResources(Input scene, Input depth, Input motionVectors, Input output)
{
	public readonly Input Scene = scene;
	public readonly Input Depth = depth; // R32_FLOAT
	public readonly Input MotionVectors = motionVectors;
	public readonly Input Output = output; // The size and format of Scene
}

/// <summary>A frame generation library. It makes frames between the previous call's scene and this call's.</summary>
public unsafe interface IFrameGenerator: IDisposable
{
	/// <summary>Status name, with the library's version</summary>
	string Name { get; }

	/// <summary>Call on the render thread. output has the size and format of inputs.Scene.</summary>
	void Generate(ID3D11DeviceContext* context, in FrameInputs inputs, ID3D11Texture2D* output);

	/// <summary>Only one thread at a time can call this method. frameId increases by one for each call.</summary>
	void Record(ID3D12GraphicsCommandList* list, in FrameInputs inputs, in GenerationResources resources, bool reset, ulong frameId);
}
