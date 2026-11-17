using System;

using TerraFX.Interop.DirectX;

using UpscaleBuddy.D3D12;

using static TerraFX.Interop.DirectX.DXGI_FORMAT;

namespace UpscaleBuddy.Game;

/// <summary>The game's ToneAdjust part loads the same files.</summary>
public sealed unsafe class ColorFilterLuts: IDisposable
{
	private const uint Size = 16;
	private const int HeaderSize = 80;
	private const int TexelBytes = (int)(Size * Size * Size * 4);
	private static readonly string[] Paths =
	[
		"common/graphics/texture/-output_lut_p.tex",
		"common/graphics/texture/-output_lut_d.tex",
		"common/graphics/texture/-output_lut_t.tex",
	];

	private readonly ID3D12Resource*[] luts = new ID3D12Resource*[3];

	/// <summary>read returns the bytes of a game file, or null when the file is missing.</summary>
	public ColorFilterLuts(ID3D12Device* device, ID3D12CommandQueue* queue, Func<string, byte[]?> read)
	{
		for (int i = 0; i < Paths.Length; i++)
		{
			byte[]? file = read(Paths[i]);
			if (file == null || file.Length < HeaderSize + TexelBytes)
			{
				this.Dispose();
				return;
			}

			// The texels follow the header. They are one 16^3 B8G8R8X8_UNORM mip.
			this.luts[i] = Upload.Texture3D(device, queue, DXGI_FORMAT_B8G8R8X8_UNORM, Size, file.AsSpan(HeaderSize, TexelBytes), 4);
		}

		this.Loaded = true;
	}

	public bool Loaded { get; private set; }

	/// <summary>This is the mapping of the ToneAdjust part, vtable slot 6.</summary>
	public static int Select(int index) => index is 1 or 2 ? index - 1 : 2;

	/// <summary>Returns null when Loaded is false.</summary>
	public ID3D12Resource* For(int index) => this.luts[Select(index)];

	public void Dispose()
	{
		for (int i = 0; i < this.luts.Length; i++)
		{
			Dx.Release(this.luts[i]);
			this.luts[i] = null;
		}

		this.Loaded = false;
	}
}
