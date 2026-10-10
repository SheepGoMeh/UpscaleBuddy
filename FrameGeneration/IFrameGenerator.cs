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

	// World space; FSR 4 frame generation needs them, FSR 3.1 uses them from 3.1.4
	public Vector3 CameraPosition, CameraUp, CameraRight, CameraForward;
}

/// <summary>A frame generation library: frames between the previous call's scene and this one</summary>
public unsafe interface IFrameGenerator: IDisposable
{
	/// <summary>Status name, with the library's version</summary>
	string Name { get; }

	/// <summary>Render thread: the frame halfway between the previous call's scene and this call's, into output (scene size and format)</summary>
	void Generate(ID3D11DeviceContext* context, in FrameInputs inputs, ID3D11Texture2D* output);
}
