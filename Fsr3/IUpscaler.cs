using System;

using TerraFX.Interop.DirectX;

namespace UpscaleBuddy.Fsr3;

/// <summary>
/// Built-in FSR 3.1 on D3D11 or AMD's FSR DLL on D3D12
/// </summary>
public unsafe interface IUpscaler: IDisposable
{
	string Name { get; }

	uint MaxRenderWidth { get; }

	uint MaxRenderHeight { get; }

	uint UpscaleWidth { get; }

	uint UpscaleHeight { get; }

	/// <summary>GPU time breakdown, filled while DispatchParams.Measure is set</summary>
	string Timings { get; }

	/// <summary>Render thread</summary>
	void Dispatch(ID3D11DeviceContext* context, in Fsr3Upscaler.DispatchParams p);
}
