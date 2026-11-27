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
/// FSR 3.1 upscaler on D3D11, a port of ffx_fsr3upscaler.cpp from the FidelityFX SDK (MIT).
/// Shaders/Build-Shaders.ps1 builds the shaders for LDR color, inverted depth and low resolution motion vectors.
/// </summary>
public unsafe class Fsr3Upscaler: IUpscaler
{
	public struct DispatchParams
	{
		public ID3D11Texture2D* Color;
		public ID3D11Texture2D* Depth;
		public ID3D11Texture2D* MotionVectors;
		public ID3D11Texture2D* Output;

		// R8, rain and VFX coverage (Game/EffectMask), null for none
		public ID3D11Texture2D* Reactive;
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

	private sealed class Pass(ID3D11ComputeShader* shader, Binding[] bindings)
	{
		public readonly ID3D11ComputeShader* Shader = shader;
		public readonly Binding[] Bindings = bindings;
	}

	private enum BindingKind
	{
		Srv,
		Uav,
		ConstantBuffer,
	}

	/// <summary>One line of a pass's binding list. View is a SrvId, UavId or ConstantsId, by kind</summary>
	private readonly record struct Binding(BindingKind Kind, int View, uint Slot);

	// What the shaders can read and write. Dispatch fills one view per entry, each frame
	private enum SrvId
	{
		InputColor,
		InputMotionVectors,
		InputDepth,
		InputExposure,
		ReactiveMask,
		TransparencyMask,
		ReconstructedPreviousDepth,
		DilatedMotionVectors,
		DilatedDepth,
		InternalUpscaled,
		Accumulation,
		LumaHistory,
		RcasInput,
		LanczosLut,
		SpdMips,
		DilatedReactiveMasks,
		NewLocks,
		Intermediate,
		FarthestDepthMip1,
		ShadingChange,
		CurrentLuma,
		PreviousLuma,
		FrameInfo,
		Count,
	}

	private enum UavId
	{
		ReconstructedPreviousDepth,
		DilatedMotionVectors,
		DilatedDepth,
		InternalUpscaled,
		Accumulation,
		LumaHistory,
		UpscaledOutput,
		DilatedReactiveMasks,
		FrameInfo,
		SpdAtomic,
		NewLocks,
		ShadingChange,
		Intermediate,
		FarthestDepthMip1,
		CurrentLuma,
		SpdMip0,
		SpdMip1,
		SpdMip2,
		SpdMip3,
		SpdMip4,
		SpdMip5,
		Count,
	}

	private enum ConstantsId
	{
		Upscaler,
		Spd,
		Rcas,
	}

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
	private readonly Pass prepareInputs, lumaPyramid, shadingChangePyramid, shadingChangePass, prepareReactivity, lumaInstability, accumulate,
		accumulateSharpen, rcasPass;

	// Views on the game's textures by texture. They keep the textures alive, so a new texture can't reuse an address.
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
	private SpdConstants? uploadedSpd; // The content of spdBuffer
	private RcasConstants? uploadedRcas; // The content of rcasBuffer
	private readonly ID3D11SamplerState*[] samplers = new ID3D11SamplerState*[2];

	private readonly GpuTimer timer;

	private Constants constants;
	private bool firstExecution = true;
	private uint resourceFrameIndex;
	private float preExposure;

	/// <summary>
	/// The luma pyramid reads its frame info (R32G32B32A32_FLOAT) and SPD mip 5 (R16G16_FLOAT) back through UAVs.
	/// These are typed UAV loads beyond the R32 formats, which are optional below feature level 12.
	/// Without them, the device can't create the shader.
	/// </summary>
	private static void RequireSupport(ID3D11Device* device)
	{
		D3D11_FEATURE_DATA_D3D11_OPTIONS2 options = default;
		// Wine can report Windows 7 on a runtime with OPTIONS2. The query fails on a runtime without it.
#pragma warning disable CA1416
		bool additionalFormats = device->CheckFeatureSupport(D3D11_FEATURE.D3D11_FEATURE_D3D11_OPTIONS2, &options, (uint)sizeof(D3D11_FEATURE_DATA_D3D11_OPTIONS2)) >= 0 &&
		                         options.TypedUAVLoadAdditionalFormats;
#pragma warning restore CA1416
		foreach (DXGI_FORMAT format in new[] { DXGI_FORMAT_R32G32B32A32_FLOAT, DXGI_FORMAT_R16G16_FLOAT })
		{
			D3D11_FEATURE_DATA_FORMAT_SUPPORT2 support = new() { InFormat = format };
			additionalFormats &= device->CheckFeatureSupport(D3D11_FEATURE.D3D11_FEATURE_FORMAT_SUPPORT2, &support, (uint)sizeof(D3D11_FEATURE_DATA_FORMAT_SUPPORT2)) >= 0 &&
			                     (support.OutFormatSupport2 & (uint)D3D11_FORMAT_SUPPORT2.D3D11_FORMAT_SUPPORT2_UAV_TYPED_LOAD) != 0;
		}

		if (!additionalFormats)
			throw new NotSupportedException("this GPU or driver can't run the built-in FSR 3.1 (no typed UAV loads), choose AMD FSR (DLL over D3D12)");
	}

	public Fsr3Upscaler(ID3D11Device* device, uint maxRenderWidth, uint maxRenderHeight, uint upscaleWidth, uint upscaleHeight)
	{
		RequireSupport(device);
		this.device = device;
		this.maxRenderWidth = maxRenderWidth;
		this.maxRenderHeight = maxRenderHeight;
		this.upscaleWidth = upscaleWidth;
		this.upscaleHeight = upscaleHeight;

		try
		{
			this.prepareInputs = this.LoadPass("prepare_inputs");
			this.lumaPyramid = this.LoadPass("luma_pyramid");
			this.shadingChangePyramid = this.LoadPass("shading_change_pyramid");
			this.shadingChangePass = this.LoadPass("shading_change");
			this.prepareReactivity = this.LoadPass("prepare_reactivity");
			this.lumaInstability = this.LoadPass("luma_instability");
			this.accumulate = this.LoadPass("accumulate");
			this.accumulateSharpen = this.LoadPass("accumulate_sharpen");
			this.rcasPass = this.LoadPass("rcas");

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

			// Full mip chain. The passes write mips 0-5.
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

	public string Name => "FSR 3.1 (built in)";

	public string Timings => $"GPU {this.timer.Ms[0]:F3} ms";

	public uint MaxRenderWidth => this.maxRenderWidth;

	public uint MaxRenderHeight => this.maxRenderHeight;

	public uint UpscaleWidth => this.upscaleWidth;

	public uint UpscaleHeight => this.upscaleHeight;

	/// <summary>
	/// Render thread. Follows fsr3upscalerDispatch.
	/// </summary>
	public void Dispatch(ID3D11DeviceContext* context, in DispatchParams p)
	{
		if (p.Measure)
			this.timer.Begin(context);

		ID3D11ShaderResourceView* color = this.InputSrv(p.Color);
		ID3D11ShaderResourceView* depth = this.InputSrv(p.Depth);
		ID3D11ShaderResourceView* motionVectors = this.InputSrv(p.MotionVectors);
		ID3D11UnorderedAccessView* output = this.InputUav(p.Output);
		ID3D11ShaderResourceView* reactive = p.Reactive != null ? this.InputSrv(p.Reactive) : this.defaultReactivity.Srv;

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
		Dx.UploadIfChanged(context, this.spdBuffer, spd, ref this.uploadedSpd);
		Dx.UploadIfChanged(context, this.rcasBuffer, rcas, ref this.uploadedRcas);

		int currentLuma = odd ? 1 : 0;
		int previousLuma = odd ? 0 : 1;

		ID3D11ShaderResourceView** srvs = stackalloc ID3D11ShaderResourceView*[(int)SrvId.Count];
		srvs[(int)SrvId.InputColor] = color;
		srvs[(int)SrvId.InputMotionVectors] = motionVectors;
		srvs[(int)SrvId.InputDepth] = depth;
		srvs[(int)SrvId.InputExposure] = this.defaultExposure.Srv;
		srvs[(int)SrvId.ReactiveMask] = reactive;
		srvs[(int)SrvId.TransparencyMask] = this.defaultReactivity.Srv;
		srvs[(int)SrvId.ReconstructedPreviousDepth] = this.reconstructedPreviousDepth.Srv;
		srvs[(int)SrvId.DilatedMotionVectors] = this.dilatedMotionVectors.Srv;
		srvs[(int)SrvId.DilatedDepth] = this.dilatedDepth.Srv;
		srvs[(int)SrvId.InternalUpscaled] = this.upscaled[srvIndex].Srv;
		srvs[(int)SrvId.Accumulation] = this.accumulation[srvIndex].Srv;
		srvs[(int)SrvId.LumaHistory] = this.lumaHistory[srvIndex].Srv;
		srvs[(int)SrvId.RcasInput] = this.upscaled[uavIndex].Srv;
		srvs[(int)SrvId.LanczosLut] = this.lanczosLut.Srv;
		srvs[(int)SrvId.SpdMips] = this.spdMips.Srv;
		srvs[(int)SrvId.DilatedReactiveMasks] = this.dilatedReactiveMasks.Srv;
		srvs[(int)SrvId.NewLocks] = this.newLocks.Srv;
		srvs[(int)SrvId.Intermediate] = this.intermediate.Srv;
		srvs[(int)SrvId.FarthestDepthMip1] = this.farthestDepthMip1.Srv;
		srvs[(int)SrvId.ShadingChange] = this.shadingChange.Srv;
		srvs[(int)SrvId.CurrentLuma] = this.luma[currentLuma].Srv;
		srvs[(int)SrvId.PreviousLuma] = this.luma[previousLuma].Srv;
		srvs[(int)SrvId.FrameInfo] = this.frameInfo.Srv;

		ID3D11UnorderedAccessView** uavs = stackalloc ID3D11UnorderedAccessView*[(int)UavId.Count];
		uavs[(int)UavId.ReconstructedPreviousDepth] = this.reconstructedPreviousDepth.Uav;
		uavs[(int)UavId.DilatedMotionVectors] = this.dilatedMotionVectors.Uav;
		uavs[(int)UavId.DilatedDepth] = this.dilatedDepth.Uav;
		uavs[(int)UavId.InternalUpscaled] = this.upscaled[uavIndex].Uav;
		uavs[(int)UavId.Accumulation] = this.accumulation[uavIndex].Uav;
		uavs[(int)UavId.LumaHistory] = this.lumaHistory[uavIndex].Uav;
		uavs[(int)UavId.UpscaledOutput] = output;
		uavs[(int)UavId.DilatedReactiveMasks] = this.dilatedReactiveMasks.Uav;
		uavs[(int)UavId.FrameInfo] = this.frameInfo.Uav;
		uavs[(int)UavId.SpdAtomic] = this.spdAtomic.Uav;
		uavs[(int)UavId.NewLocks] = this.newLocks.Uav;
		uavs[(int)UavId.ShadingChange] = this.shadingChange.Uav;
		uavs[(int)UavId.Intermediate] = this.intermediate.Uav;
		uavs[(int)UavId.FarthestDepthMip1] = this.farthestDepthMip1.Uav;
		uavs[(int)UavId.CurrentLuma] = this.luma[currentLuma].Uav;
		for (int mip = 0; mip < SpdUavMips; mip++)
			uavs[(int)UavId.SpdMip0 + mip] = this.spdMipUavs[mip];

		uint srcX = (uint)(this.constants.RenderWidth + 7) / 8, srcY = (uint)(this.constants.RenderHeight + 7) / 8;
		uint dstX = (uint)(this.constants.UpscaleWidth + 7) / 8, dstY = (uint)(this.constants.UpscaleHeight + 7) / 8;
		uint changeX = (uint)((int)(this.constants.RenderWidth * 0.5f) + 7) / 8;
		uint changeY = (uint)((int)(this.constants.RenderHeight * 0.5f) + 7) / 8;

		this.Run(context, this.prepareInputs, srcX, srcY, srvs, uavs);
		this.Run(context, this.lumaPyramid, spdGroupsX, spdGroupsY, srvs, uavs);
		this.Run(context, this.shadingChangePyramid, spdGroupsX, spdGroupsY, srvs, uavs);
		this.Run(context, this.shadingChangePass, changeX, changeY, srvs, uavs);
		this.Run(context, this.prepareReactivity, srcX, srcY, srvs, uavs);
		this.Run(context, this.lumaInstability, srcX, srcY, srvs, uavs);
		this.Run(context, p.Sharpen ? this.accumulateSharpen : this.accumulate, dstX, dstY, srvs, uavs);
		if (p.Sharpen)
			this.Run(context, this.rcasPass, (uint)(this.constants.UpscaleWidth + 15) / 16, (uint)(this.constants.UpscaleHeight + 15) / 16, srvs, uavs);

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

	private void Run(ID3D11DeviceContext* context, Pass pass, uint x, uint y, ID3D11ShaderResourceView** srvViews,
		ID3D11UnorderedAccessView** uavViews)
	{
		ID3D11ShaderResourceView** srvs = stackalloc ID3D11ShaderResourceView*[16];
		ID3D11UnorderedAccessView** uavs = stackalloc ID3D11UnorderedAccessView*[16];
		ID3D11Buffer** buffers = stackalloc ID3D11Buffer*[4];
		uint uavCount = 0, bufferCount = 0;
		foreach ((BindingKind kind, int view, uint slot) in pass.Bindings)
		{
			switch (kind)
			{
				case BindingKind.Srv:
					srvs[slot] = srvViews[view];
					break;
				case BindingKind.Uav:
					uavs[slot] = uavViews[view];
					uavCount = Math.Max(uavCount, slot + 1);
					break;
				case BindingKind.ConstantBuffer:
					buffers[slot] = (ConstantsId)view switch
					{
						ConstantsId.Spd => this.spdBuffer, ConstantsId.Rcas => this.rcasBuffer, _ => this.constantBuffer,
					};
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
		List<Binding> bindings = [];
		using StreamReader reader = new(new MemoryStream(Dx.ReadResource($"Shaders.{name}.txt")));
		while (reader.ReadLine() is { } line)
		{
			string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
			if (parts.Length != 3)
				continue;

			uint slot = uint.Parse(parts[2]);
			switch (parts[0])
			{
				case "srv":
					bindings.Add(new Binding(BindingKind.Srv, (int)SrvNamed(parts[1]), slot));
					break;
				case "uav":
					bindings.Add(new Binding(BindingKind.Uav, (int)UavNamed(parts[1]), slot));
					break;
				case "cb":
					bindings.Add(new Binding(BindingKind.ConstantBuffer, (int)ConstantsNamed(parts[1]), slot));
					break;
			}
		}

		return new Pass(this.Own(Dx.CreateComputeShader(this.device, $"Shaders.{name}.cso")), bindings.ToArray());
	}

	private static SrvId SrvNamed(string name) => name switch
	{
		"r_input_color_jittered" => SrvId.InputColor,
		"r_input_motion_vectors" => SrvId.InputMotionVectors,
		"r_input_depth" => SrvId.InputDepth,
		"r_input_exposure" => SrvId.InputExposure,
		"r_reactive_mask" => SrvId.ReactiveMask,
		"r_transparency_and_composition_mask" => SrvId.TransparencyMask,
		"r_reconstructed_previous_nearest_depth" => SrvId.ReconstructedPreviousDepth,
		"r_dilated_motion_vectors" => SrvId.DilatedMotionVectors,
		"r_dilated_depth" => SrvId.DilatedDepth,
		"r_internal_upscaled_color" => SrvId.InternalUpscaled,
		"r_accumulation" => SrvId.Accumulation,
		"r_luma_history" => SrvId.LumaHistory,
		"r_rcas_input" => SrvId.RcasInput,
		"r_lanczos_lut" => SrvId.LanczosLut,
		"r_spd_mips" => SrvId.SpdMips,
		"r_dilated_reactive_masks" => SrvId.DilatedReactiveMasks,
		"r_new_locks" => SrvId.NewLocks,
		"r_farthest_depth" or "r_luma_instability" => SrvId.Intermediate,
		"r_farthest_depth_mip1" => SrvId.FarthestDepthMip1,
		"r_shading_change" => SrvId.ShadingChange,
		"r_current_luma" => SrvId.CurrentLuma,
		"r_previous_luma" => SrvId.PreviousLuma,
		"r_frame_info" => SrvId.FrameInfo,
		_ => throw new InvalidOperationException($"unknown SRV {name}"),
	};

	private static UavId UavNamed(string name) => name switch
	{
		"rw_reconstructed_previous_nearest_depth" => UavId.ReconstructedPreviousDepth,
		"rw_dilated_motion_vectors" => UavId.DilatedMotionVectors,
		"rw_dilated_depth" => UavId.DilatedDepth,
		"rw_internal_upscaled_color" => UavId.InternalUpscaled,
		"rw_accumulation" => UavId.Accumulation,
		"rw_luma_history" => UavId.LumaHistory,
		"rw_upscaled_output" => UavId.UpscaledOutput,
		"rw_dilated_reactive_masks" => UavId.DilatedReactiveMasks,
		"rw_frame_info" => UavId.FrameInfo,
		"rw_spd_global_atomic" => UavId.SpdAtomic,
		"rw_new_locks" => UavId.NewLocks,
		"rw_shading_change" => UavId.ShadingChange,
		"rw_farthest_depth" or "rw_luma_instability" => UavId.Intermediate,
		"rw_farthest_depth_mip1" => UavId.FarthestDepthMip1,
		"rw_current_luma" => UavId.CurrentLuma,
		"rw_spd_mip0" => UavId.SpdMip0,
		"rw_spd_mip1" => UavId.SpdMip1,
		"rw_spd_mip2" => UavId.SpdMip2,
		"rw_spd_mip3" => UavId.SpdMip3,
		"rw_spd_mip4" => UavId.SpdMip4,
		"rw_spd_mip5" => UavId.SpdMip5,
		_ => throw new InvalidOperationException($"unknown UAV {name}"),
	};

	private static ConstantsId ConstantsNamed(string name) => name switch
	{
		"cbSPD" => ConstantsId.Spd,
		"cbRCAS" => ConstantsId.Rcas,
		_ => ConstantsId.Upscaler,
	};

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
