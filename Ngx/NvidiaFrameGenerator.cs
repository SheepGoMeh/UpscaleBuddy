using System;
using System.Numerics;
using System.Runtime.InteropServices;

using TerraFX.Interop.DirectX;

using UpscaleBuddy.D3D12;
using UpscaleBuddy.FrameGeneration;

using static TerraFX.Interop.DirectX.D3D12_RESOURCE_STATES;
using static TerraFX.Interop.DirectX.DXGI_FORMAT;

namespace UpscaleBuddy.Ngx;

/// <summary>
/// NVIDIA DLSS frame generation (DLSSG) through the driver's NGX core, into an output texture of the plugin.
/// The motion vectors include the camera motion. DLSSG.MvecScale must convert them to pixels.
/// </summary>
public sealed unsafe class NvidiaFrameGenerator: IFrameGenerator
{
	private const int FeatureFrameGeneration = 11; // NVSDK_NGX_Feature_FrameGeneration
	private const uint BackbufferFormat = (uint)DXGI_FORMAT_B8G8R8A8_UNORM;
	private const int MatrixCount = 5;

	private readonly Bridge bridge;
	private readonly uint width, height, maxRenderWidth, maxRenderHeight;
	private readonly DlssFrameGenerationModel model;
	private readonly INgxCore core;
	private Matrix4x4* matrices; // ViewToClip, ClipToView, ClipToLensClip, ClipToPrevClip, PrevClipToClip
	private NgxParameter* parameters;
	private nint handle;
	private bool first = true;
	private ulong frameId;
	private FrameInputs? previous;

	// Twins of the sync path, in the formats of the inputs
	private Twin? scene, output, depth, motionVectors;

	/// <summary>The caller owns bridge and session. Both must outlive the generator.</summary>
	public NvidiaFrameGenerator(Bridge bridge, uint width, uint height, uint maxRenderWidth, uint maxRenderHeight, DlssFrameGenerationModel model,
		NgxSession session)
	{
		this.bridge = bridge;
		this.width = width;
		this.height = height;
		this.maxRenderWidth = maxRenderWidth;
		this.maxRenderHeight = maxRenderHeight;
		this.model = model;
		this.core = session.Core;
		NgxParameter* allocated = null;
		NgxException.ThrowIfFailed("NGX AllocateParameters", this.core.AllocateParameters(&allocated));
		this.parameters = allocated;
		this.matrices = (Matrix4x4*)NativeMemory.AllocZeroed((nuint)(MatrixCount * sizeof(Matrix4x4)));
		this.Name = model == DlssFrameGenerationModel.Performance ? "DLSS frame generation (NVIDIA) (Performance)" : "DLSS frame generation (NVIDIA)";
	}

	public string Name { get; }

	public void Generate(ID3D11DeviceContext* d3d11, in FrameInputs inputs, ID3D11Texture2D* result)
	{
		D3D11_TEXTURE2D_DESC depthDesc = Dx.GetDesc(inputs.Depth);
		D3D11_TEXTURE2D_DESC motionDesc = Dx.GetDesc(inputs.MotionVectors);
		Twin sceneTwin = this.scene ??= this.bridge.CreateTwin(DXGI_FORMAT_B8G8R8A8_UNORM, this.width, this.height);
		Twin outputTwin = this.output ??= this.bridge.CreateTwin(DXGI_FORMAT_B8G8R8A8_UNORM, this.width, this.height);
		Twin depthTwin = this.depth ??= this.bridge.CreateTwin(DXGI_FORMAT_R32_FLOAT, depthDesc.Width, depthDesc.Height);
		Twin? directMotion = this.bridge.Direct(inputs.MotionVectors);
		Twin motionTwin = directMotion ?? (this.motionVectors ??= this.bridge.CreateTwin(motionDesc.Format, motionDesc.Width, motionDesc.Height));

		Bridge.Copy(d3d11, sceneTwin.Texture11, inputs.Scene, sceneTwin.Input);
		if (directMotion == null)
			Bridge.Copy(d3d11, motionTwin.Texture11, inputs.MotionVectors, motionTwin.Input);
		this.bridge.CopyDepth(d3d11, inputs.Depth, depthTwin, inputs.RenderWidth, inputs.RenderHeight);

		FrameInputs frame = inputs;
		bool reset = inputs.Reset || this.first;
		this.first = false;
		ulong id = ++this.frameId;
		GenerationResources resources = new(sceneTwin.Input, depthTwin.Input, motionTwin.Input, outputTwin.Input);
		this.bridge.Execute(d3d11, list => this.Record(list, frame, resources, reset, id), false);

		Bridge.Copy(d3d11, result, outputTwin.Texture11, outputTwin.Input);
	}

	public void Record(ID3D12GraphicsCommandList* list, in FrameInputs frame, in GenerationResources resources, bool reset, ulong frameId)
	{
		NgxParameter* p = this.parameters;
		if (this.handle == 0)
			this.Create(list);

		D3D12_RESOURCE_BARRIER* barriers = stackalloc D3D12_RESOURCE_BARRIER[4];
		Transitions(barriers, resources, true);
		list->ResourceBarrier(4, barriers);

		p->SetResource("DLSSG.Backbuffer", resources.Scene.Resource);
		p->SetResource("DLSSG.Depth", resources.Depth.Resource);
		p->SetResource("DLSSG.MVecs", resources.MotionVectors.Resource);
		p->SetResource("DLSSG.OutputInterpolated", resources.Output.Resource);
		p->SetUInt("DLSSG.MultiFrameCount", 1);
		p->SetUInt("DLSSG.MultiFrameIndex", 1);

		FrameInputs? before = reset ? null : this.previous;
		DlssgCamera camera = DlssgCamera.From(frame, before, (float)frame.RenderWidth / frame.RenderHeight);
		this.matrices[0] = camera.ViewToClip;
		this.matrices[1] = camera.ClipToView;
		this.matrices[2] = Matrix4x4.Identity;
		this.matrices[3] = camera.ClipToPrevClip;
		this.matrices[4] = camera.PrevClipToClip;
		p->SetPointer("DLSSG.CameraViewToClip", &this.matrices[0]);
		p->SetPointer("DLSSG.ClipToCameraView", &this.matrices[1]);
		p->SetPointer("DLSSG.ClipToLensClip", &this.matrices[2]);
		p->SetPointer("DLSSG.ClipToPrevClip", &this.matrices[3]);
		p->SetPointer("DLSSG.PrevClipToClip", &this.matrices[4]);

		p->SetFloat("DLSSG.JitterOffsetX", frame.JitterX);
		p->SetFloat("DLSSG.JitterOffsetY", frame.JitterY);
		p->SetFloat("DLSSG.MvecScaleX", frame.MotionVectorScaleX);
		p->SetFloat("DLSSG.MvecScaleY", frame.MotionVectorScaleY);
		p->SetFloat("DLSSG.CameraPinholeOffsetX", 0);
		p->SetFloat("DLSSG.CameraPinholeOffsetY", 0);
		SetVector(p, "DLSSG.CameraPos", frame.CameraPosition);
		SetVector(p, "DLSSG.CameraUp", frame.CameraUp);
		SetVector(p, "DLSSG.CameraRight", frame.CameraRight);
		SetVector(p, "DLSSG.CameraFwd", frame.CameraForward);
		p->SetFloat("DLSSG.CameraNear", frame.CameraNear);
		p->SetFloat("DLSSG.CameraFar", frame.CameraFar);
		p->SetFloat("DLSSG.CameraFOV", frame.CameraFovY);
		p->SetFloat("DLSSG.CameraAspectRatio", (float)frame.RenderWidth / frame.RenderHeight);
		p->SetUInt("DLSSG.ColorBuffersHDR", 0);
		p->SetUInt("DLSSG.DepthInverted", 1);
		p->SetUInt("DLSSG.CameraMotionIncluded", 1);
		p->SetUInt("DLSSG.Reset", reset ? 1u : 0u);
		p->SetUInt("DLSSG.AutomodeOverrideReset", 0);
		p->SetUInt("DLSSG.NotRenderingGameFrames", 0);
		p->SetUInt("DLSSG.OrthoProjection", 0);
		p->SetFloat("DLSSG.MvecInvalidValue", float.MaxValue);
		p->SetUInt("DLSSG.MvecDilated", 0);
		p->SetUInt("DLSSG.MenuDetectionEnabled", 0);
		p->SetFloat("DLSSG.MinRelativeLinearDepthObjectSeparation", 40.0f);

		// Only the render size area of the depth twin and the motion vector twin holds data
		SetRect(p, "DLSSG.MVecsSubrect", frame.RenderWidth, frame.RenderHeight);
		SetRect(p, "DLSSG.DepthSubrect", frame.RenderWidth, frame.RenderHeight);
		SetRect(p, "DLSSG.InputBackbufferSubrect", this.width, this.height);
		SetRect(p, "DLSSG.OutputInterpolatedSubrect", this.width, this.height);

		int result = this.core.EvaluateFeature(list, this.handle, p);
		this.previous = frame;

		Transitions(barriers, resources, false);
		list->ResourceBarrier(4, barriers);
		NgxException.ThrowIfFailed("NGX EvaluateFeature (DLSSG)", result);
	}

	private void Create(ID3D12GraphicsCommandList* list)
	{
		NgxParameter* p = this.parameters;
		p->SetUInt("CreationNodeMask", 1);
		p->SetUInt("VisibilityNodeMask", 1);
		p->SetUInt("Width", this.width);
		p->SetUInt("Height", this.height);
		p->SetUInt("DLSSG.Width", this.width);
		p->SetUInt("DLSSG.Height", this.height);
		p->SetUInt("DLSSG.BackbufferFormat", BackbufferFormat);
		p->SetUInt("DLSSG.InternalWidth", this.maxRenderWidth);
		p->SetUInt("DLSSG.InternalHeight", this.maxRenderHeight);
		p->SetUInt("DLSSG.DynamicResolution", 0);
		p->SetUInt("DLSSG.LiteCandidateBlendingEnabled", this.model == DlssFrameGenerationModel.Performance ? 1u : 0u);
		nint created = 0;
		NgxException.ThrowIfFailed("NGX CreateFeature (DLSSG)", this.core.CreateFeature(list, FeatureFrameGeneration, p, &created));
		this.handle = created;
	}

	/// <summary>
	/// Before the evaluate: the inputs go from COMMON to NON_PIXEL_SHADER_RESOURCE, the output to UNORDERED_ACCESS.
	/// After it: all go back to COMMON.
	/// </summary>
	private static void Transitions(D3D12_RESOURCE_BARRIER* barriers, in GenerationResources resources, bool before)
	{
		D3D12_RESOURCE_STATES common = D3D12_RESOURCE_STATE_COMMON, read = D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE;
		D3D12_RESOURCE_STATES write = D3D12_RESOURCE_STATE_UNORDERED_ACCESS;
		barriers[0] = D3D12_RESOURCE_BARRIER.InitTransition(resources.Scene.Resource, before ? common : read, before ? read : common);
		barriers[1] = D3D12_RESOURCE_BARRIER.InitTransition(resources.Depth.Resource, before ? common : read, before ? read : common);
		barriers[2] = D3D12_RESOURCE_BARRIER.InitTransition(resources.MotionVectors.Resource, before ? common : read, before ? read : common);
		barriers[3] = D3D12_RESOURCE_BARRIER.InitTransition(resources.Output.Resource, before ? common : write, before ? write : common);
	}

	private static void SetVector(NgxParameter* p, string name, Vector3 value)
	{
		p->SetFloat(name + "X", value.X);
		p->SetFloat(name + "Y", value.Y);
		p->SetFloat(name + "Z", value.Z);
	}

	private static void SetRect(NgxParameter* p, string name, uint width, uint height)
	{
		p->SetUInt(name + "BaseX", 0);
		p->SetUInt(name + "BaseY", 0);
		p->SetUInt(name + "Width", width);
		p->SetUInt(name + "Height", height);
	}

	public void Dispose()
	{
		this.bridge.WaitIdle();
		if (this.handle != 0)
			this.core.ReleaseFeature(this.handle);
		this.handle = 0;
		if (this.parameters != null)
			this.core.DestroyParameters(this.parameters);
		this.parameters = null;
		NativeMemory.Free(this.matrices);
		this.matrices = null;
		Bridge.Release(this.scene);
		Bridge.Release(this.output);
		Bridge.Release(this.depth);
		Bridge.Release(this.motionVectors);
		this.scene = this.output = this.depth = this.motionVectors = null;
	}
}
