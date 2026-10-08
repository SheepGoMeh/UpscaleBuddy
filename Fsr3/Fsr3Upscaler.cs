using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;

using static TerraFX.Interop.DirectX.D3D11_BIND_FLAG;
using static TerraFX.Interop.DirectX.DXGI_FORMAT;

namespace UpscaleBuddy.Fsr3;

/// <summary>
/// FSR 3.1 upscaler on D3D11, port of ffx_fsr3upscaler.cpp from the FidelityFX SDK (MIT)
/// Shaders built by Shaders/Build-Shaders.ps1 for LDR color, inverted depth and low resolution motion vectors
/// </summary>
public unsafe class Fsr3Upscaler: IUpscaler
{
	public struct DispatchParams
	{
		public ID3D11Texture2D* Color;
		public ID3D11Texture2D* Depth;
		public ID3D11Texture2D* MotionVectors;
		public ID3D11Texture2D* Output;
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

	private sealed class Texture(ID3D11ShaderResourceView* srv, ID3D11UnorderedAccessView* uav)
	{
		public readonly ID3D11ShaderResourceView* Srv = srv;
		public readonly ID3D11UnorderedAccessView* Uav = uav;
	}

	private sealed class Pass(ID3D11ComputeShader* shader, List<(string Kind, string Name, uint Slot)> bindings)
	{
		public readonly ID3D11ComputeShader* Shader = shader;
		public readonly List<(string Kind, string Name, uint Slot)> Bindings = bindings;
	}

	private delegate ID3D11ShaderResourceView* SrvLookup(string name);

	private delegate ID3D11UnorderedAccessView* UavLookup(string name);

	private const int MaxQueuedFrames = 16;
	private const float FltEpsilon = 1.1920929e-7f;
	private const int SpdUavMips = 6;

	private readonly ID3D11Device* device;
	private readonly uint maxRenderWidth;
	private readonly uint maxRenderHeight;
	private readonly uint upscaleWidth;
	private readonly uint upscaleHeight;

	// Released on dispose, as IUnknown*
	private readonly List<nint> owned = [];
	private readonly Dictionary<string, Pass> passes = [];

	// Views on the game's textures by texture, they keep the textures alive so an address can't be reused
	private readonly Dictionary<nint, nint> inputSrvs = [];
	private readonly Dictionary<nint, nint> inputUavs = [];

	private readonly Texture[] accumulation = new Texture[2];
	private readonly Texture[] luma = new Texture[2];
	private readonly Texture[] upscaled = new Texture[2];
	private readonly Texture[] lumaHistory = new Texture[2];
	private readonly Texture intermediate;
	private readonly Texture shadingChange;
	private readonly Texture newLocks;
	private readonly Texture spdMips;
	private readonly ID3D11UnorderedAccessView*[] spdMipUavs = new ID3D11UnorderedAccessView*[SpdUavMips];
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
	private readonly ID3D11Buffer* constantBuffer;
	private readonly ID3D11Buffer* spdBuffer;
	private readonly ID3D11Buffer* rcasBuffer;
	private readonly ID3D11SamplerState*[] samplers = new ID3D11SamplerState*[2];

	private readonly GpuTimer timer;

	private Constants constants;
	private bool firstExecution = true;
	private uint resourceFrameIndex;
	private float preExposure;

	public Fsr3Upscaler(ID3D11Device* device, uint maxRenderWidth, uint maxRenderHeight, uint upscaleWidth, uint upscaleHeight)
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
				this.accumulation[i] = this.Create(rw, rh, DXGI_FORMAT_R8_UNORM);
				this.luma[i] = this.Create(rw, rh, DXGI_FORMAT_R16_FLOAT);
				this.upscaled[i] = this.Create(uw, uh, DXGI_FORMAT_R16G16B16A16_FLOAT);
				this.lumaHistory[i] = this.Create(rw, rh, DXGI_FORMAT_R16G16B16A16_FLOAT);
			}

			this.intermediate = this.Create(rw, rh, DXGI_FORMAT_R16_FLOAT);
			this.shadingChange = this.Create(rw / 2, rh / 2, DXGI_FORMAT_R8_UNORM);
			this.newLocks = this.Create(uw, uh, DXGI_FORMAT_R8_UNORM);
			this.farthestDepthMip1 = this.Create(rw / 2, rh / 2, DXGI_FORMAT_R16_FLOAT);
			this.spdAtomic = this.Create(1, 1, DXGI_FORMAT_R32_UINT);
			this.dilatedReactiveMasks = this.Create(rw, rh, DXGI_FORMAT_R8G8B8A8_UNORM);
			this.frameInfo = this.Create(1, 1, DXGI_FORMAT_R32G32B32A32_FLOAT);
			this.dilatedDepth = this.Create(rw, rh, DXGI_FORMAT_R32_FLOAT);
			this.dilatedMotionVectors = this.Create(rw, rh, DXGI_FORMAT_R16G16_FLOAT);
			this.reconstructedPreviousDepth = this.Create(rw, rh, DXGI_FORMAT_R32_UINT);

			// Full mip chain, passes write mips 0-5
			uint spdWidth = Math.Max(1, rw / 2), spdHeight = Math.Max(1, rh / 2);
			uint spdMipCount = (uint)Math.Floor(Math.Log2(Math.Max(spdWidth, spdHeight))) + 1;
			ID3D11Texture2D* spdTexture = this.Own(Dx.CreateTexture(device, spdWidth, spdHeight, DXGI_FORMAT_R16G16_FLOAT, spdMipCount,
				Dx.BindShaderResourceAndUav));
			this.spdMips = new Texture(this.Own(Dx.CreateSrv(device, spdTexture)), null);
			for (uint mip = 0; mip < SpdUavMips; mip++)
				this.spdMipUavs[mip] = this.Own(Dx.CreateUav(device, spdTexture, DXGI_FORMAT_R16G16_FLOAT, Math.Min(mip, spdMipCount - 1)));

			short* lut = stackalloc short[128];
			for (int i = 0; i < 128; i++)
				lut[i] = (short)Math.Round(Lanczos2(2.0f * i / 127.0f) * 32767.0f);
			this.lanczosLut = this.CreateReadOnly(128, DXGI_FORMAT_R16_SNORM, lut, 256);

			byte reactivity = 0;
			this.defaultReactivity = this.CreateReadOnly(1, DXGI_FORMAT_R8_UNORM, &reactivity, 1);
			float* exposure = stackalloc float[2] { 0, 0 };
			this.defaultExposure = this.CreateReadOnly(1, DXGI_FORMAT_R32G32_FLOAT, exposure, 8);

			this.constantBuffer = this.Own(Dx.CreateConstantBuffer(device, (uint)sizeof(Constants)));
			this.spdBuffer = this.Own(Dx.CreateConstantBuffer(device, (uint)sizeof(SpdConstants)));
			this.rcasBuffer = this.Own(Dx.CreateConstantBuffer(device, (uint)sizeof(RcasConstants)));
			this.samplers[0] = this.Own(Dx.CreateSampler(device, false));
			this.samplers[1] = this.Own(Dx.CreateSampler(device, true));
			this.timer = new GpuTimer(device, 2);
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

	public string Name => "FSR 3.1 (built-in)";

	public string Timings => $"GPU {this.timer.Ms[0]:F3} ms";

	public uint MaxRenderWidth => this.maxRenderWidth;

	public uint MaxRenderHeight => this.maxRenderHeight;

	public uint UpscaleWidth => this.upscaleWidth;

	public uint UpscaleHeight => this.upscaleHeight;

	/// <summary>
	/// Render thread, follows fsr3upscalerDispatch
	/// </summary>
	public void Dispatch(ID3D11DeviceContext* context, in DispatchParams p)
	{
		if (p.Measure)
			this.timer.Begin(context);

		ID3D11ShaderResourceView* color = this.InputSrv(p.Color);
		ID3D11ShaderResourceView* depth = this.InputSrv(p.Depth);
		ID3D11ShaderResourceView* motionVectors = this.InputSrv(p.MotionVectors);
		ID3D11UnorderedAccessView* output = this.InputUav(p.Output);

		if (this.firstExecution)
		{
			foreach (Texture texture in new[] { this.accumulation[0], this.accumulation[1], this.luma[0], this.luma[1] })
				ClearFloat(context, texture.Uav, 0);
		}

		bool odd = (this.resourceFrameIndex & 1) != 0;
		int srvIndex = odd ? 1 : 0;
		int uavIndex = odd ? 0 : 1;
		bool reset = p.Reset || this.firstExecution;
		this.firstExecution = false;

		this.UpdateConstants(p, reset);

		if (reset)
		{
			ClearFloat(context, this.accumulation[srvIndex].Uav, 0);
			foreach (ID3D11UnorderedAccessView* mip in this.spdMipUavs)
				ClearFloat(context, mip, 0);
			ClearFloat(context, this.frameInfo.Uav, -1, 1);
		}

		ClearUint(context, this.reconstructedPreviousDepth.Uav, 0); // Inverted depth
		ClearUint(context, this.spdAtomic.Uav, 0);
		foreach (ID3D11UnorderedAccessView* mip in this.spdMipUavs)
			ClearFloat(context, mip, 0);

		SpdSetup(p.RenderWidth, p.RenderHeight, out uint spdGroupsX, out uint spdGroupsY, out SpdConstants spd);
		float sharpness = MathF.Pow(2.0f, -((-2.0f * p.Sharpness) + 2.0f));
		RcasConstants rcas = new()
		{
			Config0 = BitConverter.SingleToUInt32Bits(sharpness),
			Config1 = (uint)BitConverter.HalfToUInt16Bits((Half)sharpness) * 0x10001u,
		};

		Dx.Upload(context, this.constantBuffer, this.constants);
		Dx.Upload(context, this.spdBuffer, spd);
		Dx.Upload(context, this.rcasBuffer, rcas);

		int currentLuma = odd ? 1 : 0;
		int previousLuma = odd ? 0 : 1;
		ID3D11ShaderResourceView* Srv(string name) => name switch
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
		ID3D11UnorderedAccessView* Uav(string name) => name switch
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
			this.timer.Mark(context, 1);
			this.timer.End(context);
		}

		this.resourceFrameIndex = (this.resourceFrameIndex + 1) % MaxQueuedFrames;
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

	private void Run(ID3D11DeviceContext* context, string passName, uint x, uint y, SrvLookup srv, UavLookup uav)
	{
		Pass pass = this.passes[passName];
		ID3D11ShaderResourceView** srvs = stackalloc ID3D11ShaderResourceView*[16];
		ID3D11UnorderedAccessView** uavs = stackalloc ID3D11UnorderedAccessView*[16];
		ID3D11Buffer** buffers = stackalloc ID3D11Buffer*[4];
		uint uavCount = 0, bufferCount = 0;
		foreach ((string kind, string name, uint slot) in pass.Bindings)
		{
			switch (kind)
			{
				case "srv":
					srvs[slot] = srv(name);
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
		ID3D11UnorderedAccessView** noUavs = stackalloc ID3D11UnorderedAccessView*[16];
		context->CSSetUnorderedAccessViews(0, 16, noUavs, null);
		context->CSSetShaderResources(0, 16, srvs);
		context->CSSetUnorderedAccessViews(0, Math.Max(uavCount, 1), uavs, null);
		context->CSSetConstantBuffers(0, bufferCount, buffers);
		fixed (ID3D11SamplerState** samplerStates = this.samplers)
			context->CSSetSamplers(0, 2, samplerStates);
		context->CSSetShader(pass.Shader, null, 0);
		context->Dispatch(Math.Max(1, x), Math.Max(1, y), 1);
	}

	private static void Unbind(ID3D11DeviceContext* context)
	{
		ID3D11UnorderedAccessView** noUavs = stackalloc ID3D11UnorderedAccessView*[16];
		ID3D11ShaderResourceView** noSrvs = stackalloc ID3D11ShaderResourceView*[16];
		ID3D11Buffer** noBuffers = stackalloc ID3D11Buffer*[4];
		context->CSSetUnorderedAccessViews(0, 16, noUavs, null);
		context->CSSetShaderResources(0, 16, noSrvs);
		context->CSSetConstantBuffers(0, 4, noBuffers);
		context->CSSetShader(null, null, 0);
	}

	private static void ClearFloat(ID3D11DeviceContext* context, ID3D11UnorderedAccessView* uav, float x, float y = 0)
	{
		float* values = stackalloc float[4] { x, y, 0, 0 };
		context->ClearUnorderedAccessViewFloat(uav, values);
	}

	private static void ClearUint(ID3D11DeviceContext* context, ID3D11UnorderedAccessView* uav, uint value)
	{
		uint* values = stackalloc uint[4] { value, value, value, value };
		context->ClearUnorderedAccessViewUint(uav, values);
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

	private ID3D11ShaderResourceView* InputSrv(ID3D11Texture2D* texture)
	{
		if (!this.inputSrvs.TryGetValue((nint)texture, out nint view))
		{
			view = (nint)Dx.CreateSrv(this.device, texture, Dx.ReadableFormat(Dx.GetDesc(texture).Format));
			this.inputSrvs[(nint)texture] = view;
		}

		return (ID3D11ShaderResourceView*)view;
	}

	private ID3D11UnorderedAccessView* InputUav(ID3D11Texture2D* texture)
	{
		if (!this.inputUavs.TryGetValue((nint)texture, out nint view))
		{
			view = (nint)Dx.CreateUav(this.device, texture, Dx.GetDesc(texture).Format);
			this.inputUavs[(nint)texture] = view;
		}

		return (ID3D11UnorderedAccessView*)view;
	}

	private Texture Create(uint width, uint height, DXGI_FORMAT format)
	{
		ID3D11Texture2D* texture = this.Own(Dx.CreateTexture(this.device, width, height, format, 1, Dx.BindShaderResourceAndUav));
		return new Texture(this.Own(Dx.CreateSrv(this.device, texture)), this.Own(Dx.CreateUav(this.device, texture, format)));
	}

	private Texture CreateReadOnly(uint width, DXGI_FORMAT format, void* data, uint pitch)
	{
		ID3D11Texture2D* texture = this.Own(Dx.CreateTexture(this.device, width, 1, format, 1, (uint)D3D11_BIND_SHADER_RESOURCE, data, pitch));
		return new Texture(this.Own(Dx.CreateSrv(this.device, texture)), null);
	}

	private T* Own<T>(T* obj) where T : unmanaged
	{
		this.owned.Add((nint)obj);
		return obj;
	}

	private Pass LoadPass(string name)
	{
		List<(string, string, uint)> bindings = [];
		using StreamReader reader = new(new MemoryStream(Dx.ReadResource($"Shaders.{name}.txt")));
		while (reader.ReadLine() is { } line)
		{
			string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
			if (parts.Length == 3)
				bindings.Add((parts[0], parts[1], uint.Parse(parts[2])));
		}

		return new Pass(this.Own(Dx.CreateComputeShader(this.device, $"Shaders.{name}.cso")), bindings);
	}

	public void Dispose()
	{
		foreach (nint view in this.inputSrvs.Values)
			Dx.Release((IUnknown*)view);
		foreach (nint view in this.inputUavs.Values)
			Dx.Release((IUnknown*)view);
		this.inputSrvs.Clear();
		this.inputUavs.Clear();
		this.timer?.Dispose();
		foreach (nint obj in this.owned)
			Dx.Release((IUnknown*)obj);
		this.owned.Clear();
		GC.SuppressFinalize(this);
	}
}
