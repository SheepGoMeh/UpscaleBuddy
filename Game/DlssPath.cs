using System;
using System.Collections.Generic;
using System.Diagnostics;

using Dalamud.Game.Config;
using Dalamud.Hooking;

using FFXIVClientStructs.FFXIV.Client.Graphics.PostEffect;
using FFXIVClientStructs.FFXIV.Client.Graphics.Render;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;

using UpscaleBuddy.Fsr3;

using RenderCamera = FFXIVClientStructs.FFXIV.Client.Graphics.Render.Camera;
using SceneCamera = FFXIVClientStructs.FFXIV.Client.Graphics.Scene.Camera;

namespace UpscaleBuddy.Game;

/// <summary>
/// Runs FSR 3.1 through the game's DLSS path by replacing the statically linked NGX calls
/// Only active while the game's upscaler setting is AMD FSR
/// </summary>
public unsafe class DlssPath: IDisposable
{
	private const string InitSignature = "48 89 5C 24 ?? 48 89 6C 24 ?? 48 89 74 24 ?? 41 57";
	private const string CapabilitiesSignature = "E8 ?? ?? ?? ?? 25 ?? ?? ?? ?? 3D ?? ?? ?? ?? 0F 84";
	private const string CreateSignature = "E8 ?? ?? ?? ?? 4D 8B 46 ?? 33 D2";
	private const string EvaluateSignature = "48 83 EC ?? 48 8B C1 48 8B 0D ?? ?? ?? ?? 48 85 C9 75";
	private const string ReleaseSignature = "E8 ?? ?? ?? ?? 0F B6 83 ?? ?? ?? ?? 48 89 AB";
	private const string DestroyParametersSignature = "E8 ?? ?? ?? ?? 33 C9 E8 ?? ?? ?? ?? 80 A7";
	private const string ShutdownSignature = "E8 ?? ?? ?? ?? 80 A3 ?? ?? ?? ?? ?? 48 8B 8B";

	// From PostEffectManager setup (FUN_1403528a0), only done on DLSS capable GPUs
	// Create: MemAlloc(0x188), ctor, PostEffectManager+0x4220, RenderTargetManager+0x730 = render size callback
	// Teardown: FUN_140374610, FUN_140377790(object+0x90), FreeMemory(object, 0x188)
	private const string MemAllocSignature = "E8 ?? ?? ?? ?? 48 8B F8 41 BE";
	private const string DlssObjectCtorSignature = "E8 ?? ?? ?? ?? EB ?? 49 8B C7 48 89 87 ?? ?? ?? ?? 48 8D 0D";
	private const string RenderSizeCallbackSignature = "48 8D 0D ?? ?? ?? ?? 48 8B 05 ?? ?? ?? ?? 48 89 88 ?? ?? ?? ?? 48 8B CF E8";
	private const string TeardownSignature = "E8 ?? ?? ?? ?? 48 8D 8B ?? ?? ?? ?? E8 ?? ?? ?? ?? BA ?? ?? ?? ?? 48 8B CB E8 ?? ?? ?? ?? 4C 89 BF";
	private const int DlssObjectSize = 0x188;
	private const int DlssObjectOffset = 0x4220;
	private const int RenderSizeTableOffset = 0x90;
	private const int RenderSizeCallbackOffset = 0x730;
	private const int CachedOutputSizeOffset = 0x150; // -1 makes the game query the render size again
	private const int StateFlagsOffset = 0x181; // bit 0 NGX initialized, bit 1 feature created

	private const byte UpscaleTypeFsr = 1;
	private const byte UpscaleTypeDlss = 2;
	private const uint SettingAmdFsr = 0; // GraphicsRezoUpscaleType: 0 AMD FSR, 1 NVIDIA DLSS
	private const int NgxFeatureFlagDepthInverted = 0x8;
	private const int MaxRejections = 10;
	private const int ReleaseFrames = 3;

	private delegate int InitDelegate(ulong applicationId, nint path, nint device, nint featureInfo, int version);
	private delegate int CapabilitiesDelegate(nint* parameters);
	private delegate int CreateDelegate(nint context, int feature, nint parameters, nint* handle);
	private delegate int EvaluateDelegate(nint context, nint handle, nint parameters, nint progress);
	private delegate int HandleDelegate(nint handle);
	private delegate int OptimalSettingsDelegate(nint parameters);

	private enum State
	{
		Idle,
		Active,
		Releasing,
	}

	private readonly UpscaleBuddyConfiguration configuration;
	private readonly NgxParameters parameters = new();
	private readonly Hook<InitDelegate>? initHook;
	private readonly Hook<CapabilitiesDelegate>? capabilitiesHook;
	private readonly Hook<CreateDelegate>? createHook;
	private readonly Hook<EvaluateDelegate>? evaluateHook;
	private readonly Hook<HandleDelegate>? releaseHook;
	private readonly Hook<HandleDelegate>? destroyParametersHook;
	private readonly Hook<HandleDelegate>? shutdownHook;
	private readonly delegate* unmanaged<ulong, ulong, ulong, nint> memAlloc;
	private readonly delegate* unmanaged<nint, nint> dlssObjectCtor;
	private readonly delegate* unmanaged<nint, void> dlssObjectRelease;
	private readonly delegate* unmanaged<nint, void> renderSizeTableDestroy;
	private readonly delegate* unmanaged<nint, ulong, void> freeMemory;
	private readonly nint renderSizeCallback;
	private readonly nint featureHandle = System.Runtime.InteropServices.Marshal.AllocHGlobal(16);

	// Written on the framework thread, read on the render thread
	private volatile float scale = 1.0f;
	private volatile bool sharpen;
	private volatile float sharpness;
	private volatile bool measure;
	private volatile float cameraNear = 0.1f;
	private volatile float cameraFar = 1000.0f;
	private volatile float cameraFov = 1.0f;
	private volatile bool infiniteFar;

	// Render thread
	private Fsr3Upscaler? upscaler;
	private readonly List<Fsr3Upscaler> retired = [];
	private readonly object upscalerLock = new();
	private long lastEvaluate;
	private bool loggedEvaluateError;

	// Framework thread
	private State state = State.Idle;
	private bool hooksEnabled;
	private bool createdObject;
	private nint savedRenderSizeCallback;
	private int releaseFramesLeft;
	private int rejections;

	public DlssPath(UpscaleBuddyConfiguration configuration)
	{
		this.configuration = configuration;
		this.parameters.SetNumber("SuperSampling.Available", 1);
		this.parameters.SetNumber("SuperSampling.NeedsUpdatedDriver", 0);
		this.parameters.SetNumber("SuperSampling.MinDriverVersionMajor", 0);
		this.parameters.SetNumber("SuperSampling.MinDriverVersionMinor", 0);
		this.parameters.SetNumber("SuperSampling.FeatureInitResult", 1);
		this.parameters.SetPointer("DLSSOptimalSettingsCallback", this.parameters.Callback<OptimalSettingsDelegate>(this.OptimalSettings));

		try
		{
			this.initHook = Service.GameInteropProvider.HookFromAddress<InitDelegate>(
				Service.SigScanner.ScanText(InitSignature), (_, _, _, _, _) => NgxParameters.Success);
			this.capabilitiesHook = Service.GameInteropProvider.HookFromAddress<CapabilitiesDelegate>(
				Service.SigScanner.ScanText(CapabilitiesSignature), this.CapabilitiesDetour);
			this.createHook = Service.GameInteropProvider.HookFromAddress<CreateDelegate>(
				Service.SigScanner.ScanText(CreateSignature), this.CreateDetour);
			this.evaluateHook = Service.GameInteropProvider.HookFromAddress<EvaluateDelegate>(
				Service.SigScanner.ScanText(EvaluateSignature), this.EvaluateDetour);
			this.releaseHook = Service.GameInteropProvider.HookFromAddress<HandleDelegate>(
				Service.SigScanner.ScanText(ReleaseSignature), this.ReleaseDetour);
			this.destroyParametersHook = Service.GameInteropProvider.HookFromAddress<HandleDelegate>(
				Service.SigScanner.ScanText(DestroyParametersSignature), _ => NgxParameters.Success);
			this.shutdownHook = Service.GameInteropProvider.HookFromAddress<HandleDelegate>(
				Service.SigScanner.ScanText(ShutdownSignature), _ => NgxParameters.Success);

			this.memAlloc = (delegate* unmanaged<ulong, ulong, ulong, nint>)Service.SigScanner.ScanText(MemAllocSignature);
			this.dlssObjectCtor = (delegate* unmanaged<nint, nint>)Service.SigScanner.ScanText(DlssObjectCtorSignature);
			nint lea = Service.SigScanner.ScanText(RenderSizeCallbackSignature);
			this.renderSizeCallback = lea + 7 + *(int*)(lea + 3);

			nint teardown = Service.SigScanner.ScanModule(TeardownSignature);
			this.dlssObjectRelease = (delegate* unmanaged<nint, void>)CallTarget(teardown);
			this.renderSizeTableDestroy = (delegate* unmanaged<nint, void>)CallTarget(teardown + 12);
			this.freeMemory = (delegate* unmanaged<nint, ulong, void>)CallTarget(teardown + 25);
			this.Available = true;
		}
		catch (Exception e)
		{
			this.Status = "Unavailable (signature not found)";
			Service.PluginLog.Warning(e, "DlssPath unavailable");
		}
	}

	public bool Available { get; }

	public string Status { get; private set; } = "Off";

	public uint RenderWidth { get; private set; }

	public uint RenderHeight { get; private set; }

	public uint OutputWidth { get; private set; }

	public uint OutputHeight { get; private set; }

	public bool Running => this.state == State.Active && this.OutputWidth != 0;

	public double CpuMs { get; private set; }

	public double GpuMs => this.upscaler?.GpuMs ?? 0;

	public static float Scale(UpscaleMode mode) => mode switch
	{
		UpscaleMode.NativeAa => 1.0f,
		UpscaleMode.Quality => 1.5f,
		UpscaleMode.Balanced => 1.7f,
		UpscaleMode.Performance => 2.0f,
		UpscaleMode.UltraPerformance => 3.0f,
		_ => 1.0f,
	};

	private static nint CallTarget(nint call) => call + 5 + *(int*)(call + 1);

	/// <summary>
	/// Game setting for 3D resolution scaling
	/// </summary>
	private static bool GameSettingIsFsr() =>
		Service.GameConfig.TryGet(SystemConfigOption.GraphicsRezoUpscaleType, out uint value) && value == SettingAmdFsr;

	/// <summary>
	/// Framework thread
	/// </summary>
	public void Update()
	{
		if (!this.Available)
			return;

		bool wanted = this.configuration.Mode != UpscaleMode.Off && GameSettingIsFsr();
		switch (this.state)
		{
			case State.Idle when wanted:
				this.Activate();
				break;
			case State.Active when !wanted:
				this.BeginRelease(this.configuration.Mode == UpscaleMode.Off ? "Off" : "Off (the game's upscaler is not AMD FSR)");
				break;
			case State.Active:
				this.KeepActive();
				break;
			case State.Releasing when --this.releaseFramesLeft <= 0:
				this.FinishRelease();
				break;
		}

		if (this.state == State.Idle && !wanted)
			this.Status = this.configuration.Mode == UpscaleMode.Off ? "Off" : "Off (the game's upscaler is not AMD FSR)";
	}

	private void Activate()
	{
		byte* postEffectManager = (byte*)PostEffectManager.Instance();
		byte* renderTargetManager = (byte*)RenderTargetManager.Instance();
		if (postEffectManager == null || renderTargetManager == null)
		{
			this.Status = "Waiting for the renderer";
			return;
		}

		nint dlssObject = *(nint*)(postEffectManager + DlssObjectOffset);
		if (dlssObject == 0)
		{
			dlssObject = this.memAlloc(DlssObjectSize, 0, 0);
			if (dlssObject == 0)
				return;

			*(nint*)(postEffectManager + DlssObjectOffset) = this.dlssObjectCtor(dlssObject);
			this.createdObject = true;
		}
		else
		{
			// Game's own object on NVIDIA, shut down NGX before the hooks are enabled
			if ((*(byte*)(dlssObject + StateFlagsOffset) & 1) != 0)
				this.dlssObjectRelease(dlssObject);
			*(byte*)(dlssObject + StateFlagsOffset) &= 0xFC;
			*(long*)(dlssObject + CachedOutputSizeOffset) = -1;
		}

		this.savedRenderSizeCallback = *(nint*)(renderTargetManager + RenderSizeCallbackOffset);
		*(nint*)(renderTargetManager + RenderSizeCallbackOffset) = this.renderSizeCallback;

		this.EnableHooks(true);
		this.rejections = 0;
		this.scale = 0; // Forces a render size update
		this.state = State.Active;
		this.Status = "Starting";
		this.KeepActive();
	}

	private void KeepActive()
	{
		GraphicsConfig* graphicsConfig = GraphicsConfig.Instance();

		// The game switches back to FSR when the DLSS path fails
		if (graphicsConfig->GraphicsRezoUpscaleType == UpscaleTypeDlss)
		{
			this.rejections = 0;
		}
		else if (++this.rejections >= MaxRejections)
		{
			this.configuration.Mode = UpscaleMode.Off;
			this.configuration.Save();
			this.BeginRelease("The game rejected the DLSS path; turned off");
			return;
		}

		graphicsConfig->GraphicsRezoUpscaleType = UpscaleTypeDlss;
		this.SampleCamera();
		this.sharpen = this.configuration.Sharpening;
		this.sharpness = this.configuration.Sharpness;
		this.measure = this.configuration.ShowTimings;

		float wanted = Scale(this.configuration.Mode);
		if (wanted != this.scale)
		{
			// Game recreates the feature with the new render size
			this.scale = wanted;
			nint dlssObject = *(nint*)((byte*)PostEffectManager.Instance() + DlssObjectOffset);
			if (dlssObject != 0)
				*(long*)(dlssObject + CachedOutputSizeOffset) = -1;
		}
	}

	/// <summary>
	/// Restores the game's upscaler, the DLSS object is torn down a few frames later
	/// </summary>
	private void BeginRelease(string status)
	{
		GraphicsConfig.Instance()->GraphicsRezoUpscaleType = GameSettingIsFsr() ? UpscaleTypeFsr : UpscaleTypeDlss;
		this.state = State.Releasing;
		this.releaseFramesLeft = ReleaseFrames;
		this.Status = status;
		this.OutputWidth = 0;
	}

	private void FinishRelease()
	{
		byte* postEffectManager = (byte*)PostEffectManager.Instance();
		byte* renderTargetManager = (byte*)RenderTargetManager.Instance();
		nint dlssObject = postEffectManager == null ? 0 : *(nint*)(postEffectManager + DlssObjectOffset);
		if (dlssObject != 0)
		{
			// NGX calls in here still go to the hooks
			this.dlssObjectRelease(dlssObject);
			if (this.createdObject)
			{
				this.renderSizeTableDestroy(dlssObject + RenderSizeTableOffset);
				this.freeMemory(dlssObject, DlssObjectSize);
				*(nint*)(postEffectManager + DlssObjectOffset) = 0;
			}
		}

		if (renderTargetManager != null)
			*(nint*)(renderTargetManager + RenderSizeCallbackOffset) = this.savedRenderSizeCallback;

		this.createdObject = false;
		this.EnableHooks(false);
		this.state = State.Idle;
	}

	private void SampleCamera()
	{
		CameraManager* cameraManager = CameraManager.Instance();
		SceneCamera* camera = cameraManager == null ? null : cameraManager->CurrentCamera;
		RenderCamera* renderCamera = camera == null ? null : camera->RenderCamera;
		if (renderCamera == null)
			return;

		this.cameraFov = renderCamera->FoV;
		this.cameraNear = renderCamera->NearPlane;
		this.cameraFar = renderCamera->FarPlane;
		this.infiniteFar = !renderCamera->FiniteFarPlane;
	}

	private void EnableHooks(bool enable)
	{
		if (enable == this.hooksEnabled)
			return;

		Toggle(this.initHook, enable);
		Toggle(this.capabilitiesHook, enable);
		Toggle(this.createHook, enable);
		Toggle(this.evaluateHook, enable);
		Toggle(this.releaseHook, enable);
		Toggle(this.destroyParametersHook, enable);
		Toggle(this.shutdownHook, enable);
		this.hooksEnabled = enable;
	}

	private static void Toggle<T>(Hook<T>? hook, bool enable) where T : Delegate
	{
		if (hook == null)
			return;

		if (enable)
			hook.Enable();
		else
			hook.Disable();
	}

	private int CapabilitiesDetour(nint* outParameters)
	{
		*outParameters = this.parameters.Address;
		return NgxParameters.Success;
	}

	/// <summary>
	/// Render size for the configured mode, regardless of the DLSS quality
	/// </summary>
	private int OptimalSettings(nint _)
	{
		float currentScale = Math.Max(1.0f, this.scale);
		double width = Math.Max(1, Math.Floor(this.parameters.Number("Width") / currentScale));
		double height = Math.Max(1, Math.Floor(this.parameters.Number("Height") / currentScale));
		this.parameters.SetNumber("OutWidth", width);
		this.parameters.SetNumber("OutHeight", height);
		this.parameters.SetNumber("DLSS.Get.Dynamic.Max.Render.Width", width);
		this.parameters.SetNumber("DLSS.Get.Dynamic.Max.Render.Height", height);
		this.parameters.SetNumber("DLSS.Get.Dynamic.Min.Render.Width", width);
		this.parameters.SetNumber("DLSS.Get.Dynamic.Min.Render.Height", height);
		this.parameters.SetNumber("Sharpness", 0.0);
		return NgxParameters.Success;
	}

	/// <summary>
	/// Render thread, command from FUN_140374a60
	/// </summary>
	private int CreateDetour(nint context, int feature, nint parameterObject, nint* handle)
	{
		*handle = this.featureHandle;
		this.DisposeRetired();

		uint renderWidth = (uint)this.parameters.Number("Width");
		uint renderHeight = (uint)this.parameters.Number("Height");
		uint outputWidth = (uint)this.parameters.Number("OutWidth");
		uint outputHeight = (uint)this.parameters.Number("OutHeight");
		int flags = (int)this.parameters.Number("DLSS.Feature.Create.Flags");

		lock (this.upscalerLock)
		{
			this.upscaler?.Dispose();
			this.upscaler = null;
		}

		nint device = D3D11.GetDevice(context);
		try
		{
			Fsr3Upscaler created = new(device, renderWidth, renderHeight, outputWidth, outputHeight);
			lock (this.upscalerLock)
				this.upscaler = created;

			this.RenderWidth = renderWidth;
			this.RenderHeight = renderHeight;
			this.OutputWidth = outputWidth;
			this.OutputHeight = outputHeight;
			this.Status = (flags & NgxFeatureFlagDepthInverted) == 0 ? "On (the game reports non-inverted depth, expect artifacts)" : "On";
			this.loggedEvaluateError = false;
		}
		catch (Exception e)
		{
			this.Status = $"Failed to start: {e.Message}";
			Service.PluginLog.Error(e, "FSR 3.1 context creation failed");
		}
		finally
		{
			D3D11.Release(device);
		}

		return NgxParameters.Success;
	}

	/// <summary>
	/// Render thread, command from PostEffectManager.Submit
	/// </summary>
	private int EvaluateDetour(nint context, nint handle, nint parameterObject, nint progress)
	{
		this.DisposeRetired();

		long now = Stopwatch.GetTimestamp();
		float frameTime = this.lastEvaluate == 0 ? 16.6f : (float)((now - this.lastEvaluate) * 1000.0 / Stopwatch.Frequency);
		this.lastEvaluate = now;

		Fsr3Upscaler? current;
		lock (this.upscalerLock)
			current = this.upscaler;

		Fsr3Upscaler.DispatchParams dispatch = new()
		{
			Color = this.parameters.Pointer("Color"),
			Depth = this.parameters.Pointer("Depth"),
			MotionVectors = this.parameters.Pointer("MotionVectors"),
			Output = this.parameters.Pointer("Output"),
			RenderWidth = (uint)this.parameters.Number("DLSS.Render.Subrect.Dimensions.Width"),
			RenderHeight = (uint)this.parameters.Number("DLSS.Render.Subrect.Dimensions.Height"),
			JitterX = (float)this.parameters.Number("Jitter.Offset.X"),
			JitterY = (float)this.parameters.Number("Jitter.Offset.Y"),
			MotionVectorScaleX = (float)this.parameters.Number("MV.Scale.X"),
			MotionVectorScaleY = (float)this.parameters.Number("MV.Scale.Y"),
			PreExposure = (float)this.parameters.Number("DLSS.Pre.Exposure"),
			FrameTimeMs = frameTime,
			CameraNear = this.cameraNear,
			CameraFar = this.cameraFar,
			CameraFovY = this.cameraFov,
			InfiniteFar = this.infiniteFar,
			Reset = this.parameters.Number("Reset") != 0,
			Sharpen = this.sharpen,
			Sharpness = this.sharpness,
			Measure = this.measure,
		};

		if (current == null || dispatch.Color == 0 || dispatch.Depth == 0 || dispatch.MotionVectors == 0 || dispatch.Output == 0)
			return NgxParameters.Fail;

		if (dispatch.RenderWidth == 0 || dispatch.RenderHeight == 0)
		{
			dispatch.RenderWidth = current.MaxRenderWidth;
			dispatch.RenderHeight = current.MaxRenderHeight;
		}

		try
		{
			long start = Stopwatch.GetTimestamp();
			current.Dispatch(context, dispatch);
			if (dispatch.Measure)
			{
				double ms = (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency;
				this.CpuMs = (this.CpuMs * 0.9) + (ms * 0.1);
			}
		}
		catch (Exception e)
		{
			if (!this.loggedEvaluateError)
			{
				this.loggedEvaluateError = true;
				this.Status = $"Error: {e.Message}";
				Service.PluginLog.Error(e, "FSR 3.1 dispatch failed");
			}

			return NgxParameters.Fail;
		}

		return NgxParameters.Success;
	}

	/// <summary>
	/// Framework thread, the upscaler is freed on the render thread
	/// </summary>
	private int ReleaseDetour(nint handle)
	{
		lock (this.upscalerLock)
		{
			if (this.upscaler != null)
				this.retired.Add(this.upscaler);
			this.upscaler = null;
		}

		return NgxParameters.Success;
	}

	private void DisposeRetired()
	{
		lock (this.upscalerLock)
		{
			foreach (Fsr3Upscaler old in this.retired)
				old.Dispose();
			this.retired.Clear();
		}
	}

	/// <summary>
	/// Unload, restores the game's upscaler
	/// </summary>
	public void Stop()
	{
		if (this.state == State.Active)
			this.BeginRelease("Off");
	}

	/// <summary>
	/// Unload, tears down the DLSS object
	/// </summary>
	public void Finish()
	{
		if (this.state == State.Releasing)
			this.FinishRelease();
		this.EnableHooks(false);
	}

	public void Dispose()
	{
		this.parameters.Dispose();
		this.initHook?.Dispose();
		this.capabilitiesHook?.Dispose();
		this.createHook?.Dispose();
		this.evaluateHook?.Dispose();
		this.releaseHook?.Dispose();
		this.destroyParametersHook?.Dispose();
		this.shutdownHook?.Dispose();

		lock (this.upscalerLock)
		{
			this.upscaler?.Dispose();
			this.upscaler = null;
		}

		this.DisposeRetired();
		GC.SuppressFinalize(this);
	}
}
