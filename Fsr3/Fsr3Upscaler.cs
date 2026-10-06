using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

namespace UpscaleBuddy.Fsr3;

/// <summary>
/// FSR 3.1 upscaler on D3D11, port of ffx_fsr3upscaler.cpp from the FidelityFX SDK (MIT)
/// Shaders built by Shaders/Build-Shaders.ps1 for LDR color, inverted depth and low resolution motion vectors
/// </summary>
public unsafe class Fsr3Upscaler: IDisposable
{
	public struct DispatchParams
	{
		public nint Color;
		public nint Depth;
		public nint MotionVectors;
		public nint Output;
		public uint RenderWidth;
		public uint RenderHeight;
		public float JitterX;
		public float JitterY;
		public float MotionVectorScaleX;
		public float MotionVectorScaleY;
		public float PreExposure;
		public float FrameTimeMs;
		public float CameraNear;
		public float CameraFar;
		public float CameraFovY;
		public bool InfiniteFar;
		public bool Reset;
		public bool Sharpen;
		public float Sharpness;
		public bool Measure;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct Constants
	{
		public int RenderWidth, RenderHeight;
		public int PreviousRenderWidth, PreviousRenderHeight;
		public int UpscaleWidth, UpscaleHeight;
		public int PreviousUpscaleWidth, PreviousUpscaleHeight;
		public int MaxRenderWidth, MaxRenderHeight;
		public int MaxUpscaleWidth, MaxUpscaleHeight;
		public float DeviceToViewDepth0, DeviceToViewDepth1, DeviceToViewDepth2, DeviceToViewDepth3;
		public float JitterX, JitterY;
		public float PreviousJitterX, PreviousJitterY;
		public float MotionVectorScaleX, MotionVectorScaleY;
		public float DownscaleFactorX, DownscaleFactorY;
		public float MotionVectorJitterCancellationX, MotionVectorJitterCancellationY;
		public float TanHalfFov;
		public float JitterPhaseCount;
		public float DeltaTime;
		public float DeltaPreExposure;
		public float ViewSpaceToMetersFactor;
		public float FrameIndex;
		public float VelocityFactor;
		public float ReactivenessScale;
		public float ShadingChangeScale;
		public float AccumulationAddedPerFrame;
		public float MinDisocclusionAccumulation;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct SpdConstants
	{
		public uint Mips;
		public uint NumWorkGroups;
		public uint WorkGroupOffsetX, WorkGroupOffsetY;
		public uint RenderWidth, RenderHeight;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct RcasConstants
	{
		public uint Config0, Config1, Config2, Config3;
	}

	private sealed class Texture(nint texture, nint srv, nint uav)
	{
		public readonly nint Resource = texture;
		public readonly nint Srv = srv;
		public readonly nint Uav = uav;
	}

	private sealed class Pass(nint shader, List<(string Kind, string Name, uint Slot)> bindings)
	{
		public readonly nint Shader = shader;
		public readonly List<(string Kind, string Name, uint Slot)> Bindings = bindings;
	}

	private const int MaxQueuedFrames = 16;
	private const float FltEpsilon = 1.1920929e-7f;
	private const int SpdUavMips = 6;

	private readonly nint device;
	private readonly uint maxRenderWidth;
	private readonly uint maxRenderHeight;
	private readonly uint upscaleWidth;
	private readonly uint upscaleHeight;
	private readonly List<nint> owned = [];
	private readonly Dictionary<string, Pass> passes = [];
	private readonly Dictionary<(nint, bool), nint> inputViews = [];

	private readonly Texture[] accumulation = new Texture[2];
	private readonly Texture[] luma = new Texture[2];
	private readonly Texture[] upscaled = new Texture[2];
	private readonly Texture[] lumaHistory = new Texture[2];
	private readonly Texture intermediate;
	private readonly Texture shadingChange;
	private readonly Texture newLocks;
	private readonly Texture spdMips;
	private readonly nint[] spdMipUavs = new nint[SpdUavMips];
	private readonly Texture farthestDepthMip1;
	private readonly Texture spdAtomic;
	private readonly Texture dilatedReactiveMasks;
	private readonly Texture lanczosLut;
	private readonly Texture defaultReactivity;
	private readonly Texture defaultExposure;
	private readonly Texture frameInfo;
	private readonly Texture dilatedDepth;
	private readonly Texture dilatedMotionVectors;
	private readonly Texture reconstructedPreviousDepth;
	private readonly nint constantBuffer;
	private readonly nint spdBuffer;
	private readonly nint rcasBuffer;
	private readonly nint[] samplers = new nint[2];

	// Disjoint, begin, end per slot
	private const int TimingSlots = 4;
	private readonly nint[,] timing = new nint[TimingSlots, 3];
	private readonly bool[] timingPending = new bool[TimingSlots];
	private int timingSlot;

	public double GpuMs { get; private set; }

	private Constants constants;
	private bool firstExecution = true;
	private uint resourceFrameIndex;
	private float preExposure;

	public Fsr3Upscaler(nint device, uint maxRenderWidth, uint maxRenderHeight, uint upscaleWidth, uint upscaleHeight)
	{
		this.device = device;
		this.maxRenderWidth = maxRenderWidth;
		this.maxRenderHeight = maxRenderHeight;
		this.upscaleWidth = upscaleWidth;
		this.upscaleHeight = upscaleHeight;

		try
		{
			foreach (string name in new[]
			         {
				         "prepare_inputs", "luma_pyramid", "shading_change_pyramid", "shading_change", "prepare_reactivity",
				         "luma_instability", "accumulate", "accumulate_sharpen", "rcas",
			         })
			{
				this.passes[name] = this.LoadPass(name);
			}

			uint rw = maxRenderWidth, rh = maxRenderHeight, uw = upscaleWidth, uh = upscaleHeight;
			for (int i = 0; i < 2; i++)
			{
				this.accumulation[i] = this.Create(rw, rh, D3D11.FormatR8Unorm);
				this.luma[i] = this.Create(rw, rh, D3D11.FormatR16Float);
				this.upscaled[i] = this.Create(uw, uh, D3D11.FormatR16G16B16A16Float);
				this.lumaHistory[i] = this.Create(rw, rh, D3D11.FormatR16G16B16A16Float);
			}

			this.intermediate = this.Create(rw, rh, D3D11.FormatR16Float);
			this.shadingChange = this.Create(rw / 2, rh / 2, D3D11.FormatR8Unorm);
			this.newLocks = this.Create(uw, uh, D3D11.FormatR8Unorm);
			this.farthestDepthMip1 = this.Create(rw / 2, rh / 2, D3D11.FormatR16Float);
			this.spdAtomic = this.Create(1, 1, D3D11.FormatR32Uint);
			this.dilatedReactiveMasks = this.Create(rw, rh, D3D11.FormatR8G8B8A8Unorm);
			this.frameInfo = this.Create(1, 1, D3D11.FormatR32G32B32A32Float);
			this.dilatedDepth = this.Create(rw, rh, D3D11.FormatR32Float);
			this.dilatedMotionVectors = this.Create(rw, rh, D3D11.FormatR16G16Float);
			this.reconstructedPreviousDepth = this.Create(rw, rh, D3D11.FormatR32Uint);

			// Full mip chain, passes write mips 0-5
			uint spdWidth = Math.Max(1, rw / 2), spdHeight = Math.Max(1, rh / 2);
			uint spdMipCount = (uint)Math.Floor(Math.Log2(Math.Max(spdWidth, spdHeight))) + 1;
			nint spdTexture = this.Own(D3D11.CreateTexture(device, spdWidth, spdHeight, D3D11.FormatR16G16Float, spdMipCount,
				D3D11.BindShaderResource | D3D11.BindUnorderedAccess));
			this.spdMips = new Texture(spdTexture, this.Own(D3D11.CreateSrv(device, spdTexture)), 0);
			for (uint mip = 0; mip < SpdUavMips; mip++)
				this.spdMipUavs[mip] = this.Own(D3D11.CreateUav(device, spdTexture, D3D11.FormatR16G16Float, Math.Min(mip, spdMipCount - 1)));

			short* lut = stackalloc short[128];
			for (int i = 0; i < 128; i++)
				lut[i] = (short)Math.Round(Lanczos2(2.0f * i / 127.0f) * 32767.0f);
			this.lanczosLut = this.CreateReadOnly(128, D3D11.FormatR16Snorm, lut, 256);

			byte reactivity = 0;
			this.defaultReactivity = this.CreateReadOnly(1, D3D11.FormatR8Unorm, &reactivity, 1);
			float* exposure = stackalloc float[2] { 0, 0 };
			this.defaultExposure = this.CreateReadOnly(1, D3D11.FormatR32G32Float, exposure, 8);

			this.constantBuffer = this.Own(D3D11.CreateConstantBuffer(device, (uint)sizeof(Constants)));
			this.spdBuffer = this.Own(D3D11.CreateConstantBuffer(device, (uint)sizeof(SpdConstants)));
			this.rcasBuffer = this.Own(D3D11.CreateConstantBuffer(device, (uint)sizeof(RcasConstants)));
			this.samplers[0] = this.Own(D3D11.CreateSampler(device, false));
			this.samplers[1] = this.Own(D3D11.CreateSampler(device, true));
			for (int i = 0; i < TimingSlots; i++)
			{
				this.timing[i, 0] = this.Own(D3D11.CreateQuery(device, 3)); // TIMESTAMP_DISJOINT
				this.timing[i, 1] = this.Own(D3D11.CreateQuery(device, 2)); // TIMESTAMP
				this.timing[i, 2] = this.Own(D3D11.CreateQuery(device, 2));
			}
		}
		catch
		{
			this.Dispose();
			throw;
		}

		this.constants.MaxUpscaleWidth = (int)upscaleWidth;
		this.constants.MaxUpscaleHeight = (int)upscaleHeight;
		this.constants.VelocityFactor = 1.0f;
		this.constants.ReactivenessScale = 1.0f;
		this.constants.ShadingChangeScale = 1.0f;
		this.constants.AccumulationAddedPerFrame = 1.0f / 3.0f;
		this.constants.MinDisocclusionAccumulation = -1.0f / 3.0f;
	}

	public uint MaxRenderWidth => this.maxRenderWidth;

	public uint MaxRenderHeight => this.maxRenderHeight;

	public uint UpscaleWidth => this.upscaleWidth;

	public uint UpscaleHeight => this.upscaleHeight;

	/// <summary>
	/// Render thread, follows fsr3upscalerDispatch
	/// </summary>
	public void Dispatch(nint context, in DispatchParams p)
	{
		this.timingSlot = (this.timingSlot + 1) % TimingSlots;
		int slot = this.timingSlot;
		if (this.timingPending[slot])
			this.ReadTiming(context, slot);
		if (p.Measure)
		{
			D3D11.Begin(context, this.timing[slot, 0]);
			D3D11.End(context, this.timing[slot, 1]);
		}

		nint color = this.InputView(p.Color, false);
		nint depth = this.InputView(p.Depth, false);
		nint motionVectors = this.InputView(p.MotionVectors, false);
		nint output = this.InputView(p.Output, true);

		if (this.firstExecution)
		{
			foreach (Texture texture in new[] { this.accumulation[0], this.accumulation[1], this.luma[0], this.luma[1] })
				D3D11.ClearFloat(context, texture.Uav, 0);
		}

		bool odd = (this.resourceFrameIndex & 1) != 0;
		int srvIndex = odd ? 1 : 0;
		int uavIndex = odd ? 0 : 1;
		bool reset = p.Reset || this.firstExecution;
		this.firstExecution = false;

		this.UpdateConstants(p, reset);

		if (reset)
		{
			D3D11.ClearFloat(context, this.accumulation[srvIndex].Uav, 0);
			foreach (nint mip in this.spdMipUavs)
				D3D11.ClearFloat(context, mip, 0);
			D3D11.ClearFloat(context, this.frameInfo.Uav, -1, 1);
		}

		D3D11.ClearUint(context, this.reconstructedPreviousDepth.Uav, 0); // Inverted depth
		D3D11.ClearUint(context, this.spdAtomic.Uav, 0);
		foreach (nint mip in this.spdMipUavs)
			D3D11.ClearFloat(context, mip, 0);

		SpdSetup(p.RenderWidth, p.RenderHeight, out uint spdGroupsX, out uint spdGroupsY, out SpdConstants spd);
		float sharpness = MathF.Pow(2.0f, -((-2.0f * p.Sharpness) + 2.0f));
		RcasConstants rcas = new()
		{
			Config0 = BitConverter.SingleToUInt32Bits(sharpness),
			Config1 = (uint)BitConverter.HalfToUInt16Bits((Half)sharpness) * 0x10001u,
		};

		D3D11.Upload(context, this.constantBuffer, this.constants);
		D3D11.Upload(context, this.spdBuffer, spd);
		D3D11.Upload(context, this.rcasBuffer, rcas);

		int currentLuma = odd ? 1 : 0;
		int previousLuma = odd ? 0 : 1;
		nint Srv(string name) => name switch
		{
			"r_input_color_jittered" => color,
			"r_input_motion_vectors" => motionVectors,
			"r_input_depth" => depth,
			"r_input_exposure" => this.defaultExposure.Srv,
			"r_reactive_mask" or "r_transparency_and_composition_mask" => this.defaultReactivity.Srv,
			"r_reconstructed_previous_nearest_depth" => this.reconstructedPreviousDepth.Srv,
			"r_dilated_motion_vectors" => this.dilatedMotionVectors.Srv,
			"r_dilated_depth" => this.dilatedDepth.Srv,
			"r_internal_upscaled_color" => this.upscaled[srvIndex].Srv,
			"r_accumulation" => this.accumulation[srvIndex].Srv,
			"r_luma_history" => this.lumaHistory[srvIndex].Srv,
			"r_rcas_input" => this.upscaled[uavIndex].Srv,
			"r_lanczos_lut" => this.lanczosLut.Srv,
			"r_spd_mips" => this.spdMips.Srv,
			"r_dilated_reactive_masks" => this.dilatedReactiveMasks.Srv,
			"r_new_locks" => this.newLocks.Srv,
			"r_farthest_depth" or "r_luma_instability" => this.intermediate.Srv,
			"r_farthest_depth_mip1" => this.farthestDepthMip1.Srv,
			"r_shading_change" => this.shadingChange.Srv,
			"r_current_luma" => this.luma[currentLuma].Srv,
			"r_previous_luma" => this.luma[previousLuma].Srv,
			"r_frame_info" => this.frameInfo.Srv,
			_ => throw new InvalidOperationException($"unknown SRV {name}"),
		};
		nint Uav(string name) => name switch
		{
			"rw_reconstructed_previous_nearest_depth" => this.reconstructedPreviousDepth.Uav,
			"rw_dilated_motion_vectors" => this.dilatedMotionVectors.Uav,
			"rw_dilated_depth" => this.dilatedDepth.Uav,
			"rw_internal_upscaled_color" => this.upscaled[uavIndex].Uav,
			"rw_accumulation" => this.accumulation[uavIndex].Uav,
			"rw_luma_history" => this.lumaHistory[uavIndex].Uav,
			"rw_upscaled_output" => output,
			"rw_dilated_reactive_masks" => this.dilatedReactiveMasks.Uav,
			"rw_frame_info" => this.frameInfo.Uav,
			"rw_spd_global_atomic" => this.spdAtomic.Uav,
			"rw_new_locks" => this.newLocks.Uav,
			"rw_shading_change" => this.shadingChange.Uav,
			"rw_farthest_depth" or "rw_luma_instability" => this.intermediate.Uav,
			"rw_farthest_depth_mip1" => this.farthestDepthMip1.Uav,
			"rw_current_luma" => this.luma[currentLuma].Uav,
			_ when name.StartsWith("rw_spd_mip") => this.spdMipUavs[name[^1] - '0'],
			_ => throw new InvalidOperationException($"unknown UAV {name}"),
		};

		uint srcX = (uint)(this.constants.RenderWidth + 7) / 8, srcY = (uint)(this.constants.RenderHeight + 7) / 8;
		uint dstX = (uint)(this.constants.UpscaleWidth + 7) / 8, dstY = (uint)(this.constants.UpscaleHeight + 7) / 8;
		uint changeX = (uint)((int)(this.constants.RenderWidth * 0.5f) + 7) / 8;
		uint changeY = (uint)((int)(this.constants.RenderHeight * 0.5f) + 7) / 8;

		this.Run(context, "prepare_inputs", srcX, srcY, Srv, Uav);
		this.Run(context, "luma_pyramid", spdGroupsX, spdGroupsY, Srv, Uav);
		this.Run(context, "shading_change_pyramid", spdGroupsX, spdGroupsY, Srv, Uav);
		this.Run(context, "shading_change", changeX, changeY, Srv, Uav);
		this.Run(context, "prepare_reactivity", srcX, srcY, Srv, Uav);
		this.Run(context, "luma_instability", srcX, srcY, Srv, Uav);
		this.Run(context, p.Sharpen ? "accumulate_sharpen" : "accumulate", dstX, dstY, Srv, Uav);
		if (p.Sharpen)
			this.Run(context, "rcas", (uint)(this.constants.UpscaleWidth + 15) / 16, (uint)(this.constants.UpscaleHeight + 15) / 16, Srv, Uav);

		Unbind(context);
		if (p.Measure)
		{
			D3D11.End(context, this.timing[slot, 2]);
			D3D11.End(context, this.timing[slot, 0]);
			this.timingPending[slot] = true;
		}
		this.resourceFrameIndex = (this.resourceFrameIndex + 1) % MaxQueuedFrames;
	}

	private void ReadTiming(nint context, int slot)
	{
		ulong* disjoint = stackalloc ulong[2];
		ulong begin, end;
		if (!D3D11.GetData(context, this.timing[slot, 0], disjoint, 16) || ((uint*)disjoint)[2] != 0 ||
		    !D3D11.GetData(context, this.timing[slot, 1], &begin, 8) || !D3D11.GetData(context, this.timing[slot, 2], &end, 8))
			return;

		this.timingPending[slot] = false;
		this.GpuMs = (this.GpuMs * 0.9) + ((end - begin) * 1000.0 / disjoint[0] * 0.1);
	}

	private void UpdateConstants(in DispatchParams p, bool reset)
	{
		ref Constants c = ref this.constants;
		c.PreviousJitterX = c.JitterX;
		c.PreviousJitterY = c.JitterY;
		c.JitterX = p.JitterX;
		c.JitterY = p.JitterY;

		c.PreviousRenderWidth = c.RenderWidth;
		c.PreviousRenderHeight = c.RenderHeight;
		c.RenderWidth = (int)p.RenderWidth;
		c.RenderHeight = (int)p.RenderHeight;
		c.MaxRenderWidth = (int)this.maxRenderWidth;
		c.MaxRenderHeight = (int)this.maxRenderHeight;

		float aspect = p.RenderWidth / (float)p.RenderHeight;
		float horizontalFov = MathF.Atan(MathF.Tan(p.CameraFovY / 2) * aspect) * 2;
		c.TanHalfFov = MathF.Tan(horizontalFov * 0.5f);
		c.ViewSpaceToMetersFactor = 1.0f;

		// setupDeviceDepthToViewSpaceDepthParams
		float near = MathF.Min(p.CameraNear, p.CameraFar), far = MathF.Max(p.CameraNear, p.CameraFar);
		(near, far) = (far, near);
		float q = far / (near - far);
		c.DeviceToViewDepth0 = -1.0f * (p.InfiniteFar ? FltEpsilon : q);
		c.DeviceToViewDepth1 = p.InfiniteFar ? far : q * near;
		float cotHalfFovY = MathF.Cos(0.5f * p.CameraFovY) / MathF.Sin(0.5f * p.CameraFovY);
		c.DeviceToViewDepth2 = 1.0f / (cotHalfFovY / aspect);
		c.DeviceToViewDepth3 = 1.0f / cotHalfFovY;

		c.PreviousUpscaleWidth = c.UpscaleWidth;
		c.PreviousUpscaleHeight = c.UpscaleHeight;
		c.UpscaleWidth = (int)this.upscaleWidth;
		c.UpscaleHeight = (int)this.upscaleHeight;
		c.DownscaleFactorX = c.RenderWidth / (float)c.UpscaleWidth;
		c.DownscaleFactorY = c.RenderHeight / (float)c.UpscaleHeight;

		float previousPreExposure = this.preExposure;
		this.preExposure = p.PreExposure != 0.0f ? p.PreExposure : 1.0f;
		c.DeltaPreExposure = previousPreExposure > 0.0f ? this.preExposure / previousPreExposure : 1.0f;

		// Low resolution motion vectors, no jitter cancellation
		c.MotionVectorScaleX = p.MotionVectorScaleX / c.RenderWidth;
		c.MotionVectorScaleY = p.MotionVectorScaleY / c.RenderHeight;

		int phaseCount = (int)(8.0f * MathF.Pow(c.UpscaleWidth / (float)p.RenderWidth, 2.0f));
		if (reset || c.JitterPhaseCount == 0)
			c.JitterPhaseCount = phaseCount;
		else if (phaseCount > c.JitterPhaseCount)
			c.JitterPhaseCount++;
		else if (phaseCount < c.JitterPhaseCount)
			c.JitterPhaseCount--;

		c.DeltaTime = Math.Clamp(p.FrameTimeMs / 1000.0f, 0.0f, 1.0f);
		c.FrameIndex = reset ? 0.0f : c.FrameIndex + 1.0f;
	}

	private void Run(nint context, string passName, uint x, uint y, Func<string, nint> srv, Func<string, nint> uav)
	{
		Pass pass = this.passes[passName];
		nint* srvs = stackalloc nint[16];
		nint* uavs = stackalloc nint[16];
		nint* buffers = stackalloc nint[4];
		uint srvCount = 0, uavCount = 0, bufferCount = 0;
		for (int i = 0; i < 16; i++)
			srvs[i] = uavs[i] = 0;
		for (int i = 0; i < 4; i++)
			buffers[i] = 0;

		foreach ((string kind, string name, uint slot) in pass.Bindings)
		{
			switch (kind)
			{
				case "srv":
					srvs[slot] = srv(name);
					srvCount = Math.Max(srvCount, slot + 1);
					break;
				case "uav":
					uavs[slot] = uav(name);
					uavCount = Math.Max(uavCount, slot + 1);
					break;
				case "cb":
					buffers[slot] = name switch { "cbSPD" => this.spdBuffer, "cbRCAS" => this.rcasBuffer, _ => this.constantBuffer };
					bufferCount = Math.Max(bufferCount, slot + 1);
					break;
			}
		}

		// Unbind the previous outputs before using them as inputs
		nint* none = stackalloc nint[16];
		for (int i = 0; i < 16; i++)
			none[i] = 0;
		D3D11.SetUnorderedAccessViews(context, none, 16);
		D3D11.SetShaderResources(context, srvs, 16);
		D3D11.SetUnorderedAccessViews(context, uavs, Math.Max(uavCount, 1));
		D3D11.SetConstantBuffers(context, buffers, bufferCount);
		fixed (nint* sampler = this.samplers)
			D3D11.SetSamplers(context, sampler, 2);
		D3D11.SetShader(context, pass.Shader);
		D3D11.Dispatch(context, x, y);
	}

	private static void Unbind(nint context)
	{
		nint* none = stackalloc nint[16];
		for (int i = 0; i < 16; i++)
			none[i] = 0;
		D3D11.SetUnorderedAccessViews(context, none, 16);
		D3D11.SetShaderResources(context, none, 16);
		D3D11.SetConstantBuffers(context, none, 4);
		D3D11.SetShader(context, 0);
	}

	/// <summary>ffxSpdSetup for (0, 0, width, height)</summary>
	private static void SpdSetup(uint width, uint height, out uint groupsX, out uint groupsY, out SpdConstants spd)
	{
		groupsX = ((width - 1) / 64) + 1;
		groupsY = ((height - 1) / 64) + 1;
		spd = new SpdConstants
		{
			NumWorkGroups = groupsX * groupsY,
			Mips = (uint)Math.Min(Math.Floor(Math.Log2(Math.Max(width, height))), 12),
			RenderWidth = width,
			RenderHeight = height,
		};
	}

	private static float Lanczos2(float x) =>
		MathF.Abs(x) < 1e-6f ? 1.0f : (MathF.Sin(MathF.PI * x) / (MathF.PI * x)) * (MathF.Sin(0.5f * MathF.PI * x) / (0.5f * MathF.PI * x));

	/// <summary>Cached views on the game's textures</summary>
	private nint InputView(nint texture, bool unordered)
	{
		if (this.inputViews.TryGetValue((texture, unordered), out nint view))
			return view;

		D3D11.Texture2DDesc desc = D3D11.GetDesc(texture);
		uint format = desc.Format switch
		{
			44 => D3D11.FormatR24UnormX8Typeless, // R24G8_TYPELESS
			39 => D3D11.FormatR32Float, // R32_TYPELESS
			_ => desc.Format,
		};
		view = unordered ? D3D11.CreateUav(this.device, texture, format) : D3D11.CreateSrv(this.device, texture, format);
		this.inputViews[(texture, unordered)] = view;
		return view;
	}

	private Texture Create(uint width, uint height, uint format)
	{
		nint texture = this.Own(D3D11.CreateTexture(this.device, width, height, format, 1, D3D11.BindShaderResource | D3D11.BindUnorderedAccess));
		return new Texture(texture, this.Own(D3D11.CreateSrv(this.device, texture)), this.Own(D3D11.CreateUav(this.device, texture, format)));
	}

	private Texture CreateReadOnly(uint width, uint format, void* data, uint pitch)
	{
		nint texture = this.Own(D3D11.CreateTexture(this.device, width, 1, format, 1, D3D11.BindShaderResource, data, pitch));
		return new Texture(texture, this.Own(D3D11.CreateSrv(this.device, texture)), 0);
	}

	private nint Own(nint obj)
	{
		this.owned.Add(obj);
		return obj;
	}

	private Pass LoadPass(string name)
	{
		byte[] bytecode = ReadResource($"Shaders.{name}.cso");
		List<(string, string, uint)> bindings = [];
		using StreamReader reader = new(new MemoryStream(ReadResource($"Shaders.{name}.txt")));
		while (reader.ReadLine() is { } line)
		{
			string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
			if (parts.Length == 3)
				bindings.Add((parts[0], parts[1], uint.Parse(parts[2])));
		}

		return new Pass(this.Own(D3D11.CreateComputeShader(this.device, bytecode)), bindings);
	}

	private static byte[] ReadResource(string name)
	{
		using Stream stream = typeof(Fsr3Upscaler).Assembly.GetManifestResourceStream(name) ??
		                      throw new InvalidOperationException($"missing resource {name}");
		using MemoryStream memory = new();
		stream.CopyTo(memory);
		return memory.ToArray();
	}

	public void Dispose()
	{
		foreach (nint view in this.inputViews.Values)
			D3D11.Release(view);
		this.inputViews.Clear();
		foreach (nint obj in this.owned)
			D3D11.Release(obj);
		this.owned.Clear();
		GC.SuppressFinalize(this);
	}
}
