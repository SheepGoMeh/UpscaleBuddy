using System;

using UpscaleBuddy.D3D12;
using UpscaleBuddy.Ffx;
using UpscaleBuddy.Ngx;

namespace UpscaleBuddy.FrameGeneration;

/// <summary>The values that create a generator. A different value needs a new generator.</summary>
public readonly record struct GeneratorShape(uint Width, uint Height, uint MaxRenderWidth, uint MaxRenderHeight, DlssFrameGenerationModel Model);

/// <summary>
/// Creates the frame generator: NVIDIA DLSS frame generation when NvidiaRuledOut is null and NGX reports it
/// available, else AMD's.
/// </summary>
public static class FrameGenerators
{
	private static readonly object Gate = new();
	private static string? nvidiaRuledOut;

	/// <summary>Receives the reason why the plugin does not use NVIDIA frame generation.</summary>
	public static Action<string>? Log { get; set; }

	/// <summary>Null while the plugin can use NVIDIA frame generation.</summary>
	public static string? NvidiaRuledOut
	{
		get
		{
			lock (Gate)
				return nvidiaRuledOut;
		}
	}

	public static void RuleOutNvidia(string reason)
	{
		lock (Gate)
		{
			if (nvidiaRuledOut != null)
				return;
			nvidiaRuledOut = reason;
		}

		Log?.Invoke($"NVIDIA DLSS frame generation is off until the plugin reloads: {reason}");
	}

	/// <summary>Only the local checks call this.</summary>
	internal static void ResetForChecks()
	{
		lock (Gate)
			nvidiaRuledOut = null;
	}

	public static unsafe IFrameGenerator Create(Bridge bridge, uint width, uint height, uint maxRenderWidth, uint maxRenderHeight, bool infiniteFar,
		DlssFrameGenerationModel model, string ngxDataPath)
	{
		if (NvidiaRuledOut == null)
		{
			NgxSession? session = NgxSession.Get(bridge.Device12, ngxDataPath, out string reason);
			if (session == null)
			{
				RuleOutNvidia(reason);
			}
			else
			{
				try
				{
					return new NvidiaFrameGenerator(bridge, width, height, maxRenderWidth, maxRenderHeight, model, session);
				}
				catch (NgxException e)
				{
					RuleOutNvidia(e.Message);
				}
			}
		}

		return new FfxFrameGenerator(bridge, width, height, maxRenderWidth, maxRenderHeight, infiniteFar);
	}
}
