using System;
using System.Numerics;

using UpscaleBuddy.FrameGeneration;

namespace UpscaleBuddy.Ngx;

/// <summary>
/// The camera matrices of DLSS frame generation, without jitter. Row-vector layout (v * M), as System.Numerics.
/// View space is right-handed and looks along -Z. The clip depth uses reverse-Z: the near plane gives 1, the far plane
/// gives 0.
/// </summary>
public readonly record struct DlssgCamera(Matrix4x4 ViewToClip, Matrix4x4 ClipToView, Matrix4x4 ClipToPrevClip, Matrix4x4 PrevClipToClip)
{
	/// <summary>previous is the camera of the previous frame. Null gives identity matrices between the frames.</summary>
	public static DlssgCamera From(in FrameInputs current, in FrameInputs? previous, float aspect)
	{
		Matrix4x4 viewToClip = Projection(current, aspect);
		Matrix4x4.Invert(viewToClip, out Matrix4x4 clipToView);
		if (previous is not { } before)
			return new DlssgCamera(viewToClip, clipToView, Matrix4x4.Identity, Matrix4x4.Identity);

		Matrix4x4.Invert(WorldToView(current), out Matrix4x4 viewToWorld);
		Matrix4x4 clipToPrevClip = clipToView * viewToWorld * WorldToView(before) * Projection(before, aspect);
		Matrix4x4.Invert(clipToPrevClip, out Matrix4x4 prevClipToClip);
		return new DlssgCamera(viewToClip, clipToView, clipToPrevClip, prevClipToClip);
	}

	/// <summary>Reverse-Z perspective. An infinite far plane maps infinity to 0.</summary>
	private static Matrix4x4 Projection(in FrameInputs camera, float aspect)
	{
		float yScale = 1.0f / MathF.Tan(camera.CameraFovY / 2.0f);
		float near = camera.CameraNear, far = camera.CameraFar;
		float a = camera.InfiniteFar ? 0.0f : near / (far - near);
		float b = camera.InfiniteFar ? near : near * far / (far - near);
		return new Matrix4x4(
			yScale / aspect, 0, 0, 0,
			0, yScale, 0, 0,
			0, 0, a, -1,
			0, 0, b, 0);
	}

	private static Matrix4x4 WorldToView(in FrameInputs camera)
	{
		Vector3 right = camera.CameraRight, up = camera.CameraUp, back = -camera.CameraForward, position = camera.CameraPosition;
		return new Matrix4x4(
			right.X, up.X, back.X, 0,
			right.Y, up.Y, back.Y, 0,
			right.Z, up.Z, back.Z, 0,
			-Vector3.Dot(position, right), -Vector3.Dot(position, up), -Vector3.Dot(position, back), 1);
	}
}
