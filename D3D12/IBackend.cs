using System;

using TerraFX.Interop.DirectX;

using UpscaleBuddy.Fsr3;

namespace UpscaleBuddy.D3D12;

/// <summary>
/// A texture on the D3D12 device: the game's own when it's shared, otherwise our twin holding a copy
/// </summary>
public readonly unsafe struct Input(ID3D12Resource* resource, DXGI_FORMAT format, uint width, uint height, bool unorderedAccess)
{
	public readonly ID3D12Resource* Resource = resource;
	public readonly DXGI_FORMAT Format = format;
	public readonly uint Width = width;
	public readonly uint Height = height;
	public readonly bool UnorderedAccess = unorderedAccess;
}

/// <summary>
/// An upscaler library on the D3D12 device, D3D12Upscaler does everything around it
/// </summary>
public unsafe interface IBackend: IDisposable
{
	/// <summary>Status name, with the library's version</summary>
	string Name { get; }

	/// <summary>Short name for the timings</summary>
	string Label { get; }

	/// <summary>
	/// Records the upscale into the open command list; every texture arrives in COMMON state and has to be left in it.
	/// Throws when the library fails, the list is then dropped
	/// </summary>
	void Record(ID3D12GraphicsCommandList* list, in Input color, in Input motionVectors, in Input depth, in Input output,
		in Fsr3Upscaler.DispatchParams p, uint upscaleWidth, uint upscaleHeight);
}

/// <summary>Creates the backend on the D3D12 device D3D12Upscaler made</summary>
public unsafe delegate IBackend BackendFactory(ID3D12Device* device);
