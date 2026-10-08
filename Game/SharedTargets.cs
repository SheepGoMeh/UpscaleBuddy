using System;
using System.Runtime.InteropServices;

using FFXIVClientStructs.FFXIV.Client.Graphics.Kernel;
using FFXIVClientStructs.FFXIV.Client.Graphics.Render;

using TerraFX.Interop.DirectX;

using KernelTexture = FFXIVClientStructs.FFXIV.Client.Graphics.Kernel.Texture;

namespace UpscaleBuddy.Game;

/// <summary>
/// Gives SceneInput, SceneOutput and velocity NT shared D3D11 textures, so D3D12 opens them without copies
/// Uses the game's texture aliasing: a Kernel::Texture whose source (+0xC0) is set takes the source's ID3D11Texture2D
/// instead of creating one and builds its own views on it (sub_140218520). After the game recreates its render targets
/// (Device.OnResizeCreate, PostTick with the render thread idle) each target is released and recreated on a source holding
/// our shared texture. The game's own release pass (Device.OnResizeDestroy, sub_140216C00) drops the source again, so a
/// reallocation while disabled gives the game its own textures back
/// </summary>
public unsafe class SharedTargets: IDisposable
{
	// Kernel::Texture: releases the D3D11 resource, views and source
	private const string ReleaseResourcesSignature = "40 53 41 54 48 83 EC 28 48 8B 91 B8 00 00 00";

	// Kernel::Texture: sets the size (and mips, and format unless 0), then creates the D3D11 resource and views; on failure releases
	private const string ResizeSignature = "40 53 48 83 EC 20 48 8B 02 48 8B D9 48 89 41 38 48 8B 02";

	private const int SourceTextureOffset = 0xC0; // Kernel::Texture*, the texture whose D3D11 resource this one aliases

	// RenderTargetManager: SceneInput, SceneOutput, velocity, and its own OnResizeCreate callback index
	private static readonly int[] TargetOffsets = [0x68, 0x258, 0x650];
	private const int RenderTargetManagerCreateCallbackOffset = 0x46C;

	private const uint AllocationClass = 3; // What RenderTargetManager.Initialize creates its targets with

	private delegate byte CallbackDelegate(void* context);

	private readonly delegate* unmanaged<KernelTexture*, void> releaseResources;
	private readonly delegate* unmanaged<KernelTexture*, uint*, byte, uint, byte> resize;

	// Kept alive here, the game calls it from PostTick
	private readonly CallbackDelegate recreateCallback;
	private readonly int callbackIndex;
	private volatile bool enabled;

	public SharedTargets()
	{
		Device* device = Device.Instance();
		RenderTargetManager* renderTargetManager = RenderTargetManager.Instance();
		if (device == null || renderTargetManager == null)
			throw new InvalidOperationException("the renderer isn't created yet");

		this.releaseResources = (delegate* unmanaged<KernelTexture*, void>)Service.SigScanner.ScanText(ReleaseResourcesSignature);
		this.resize = (delegate* unmanaged<KernelTexture*, uint*, byte, uint, byte>)Service.SigScanner.ScanText(ResizeSignature);

		// Has to run after RenderTargetManager's own callback, which recreates the targets
		this.recreateCallback = this.OnResizeCreate;
		this.callbackIndex = device->OnResizeCreate->AddCallback(
			(delegate* unmanaged<void*, bool>)Marshal.GetFunctionPointerForDelegate(this.recreateCallback), null);
		int gameIndex = *(int*)((byte*)renderTargetManager + RenderTargetManagerCreateCallbackOffset);
		if (this.callbackIndex < 0 || this.callbackIndex <= gameIndex)
		{
			if (this.callbackIndex >= 0)
				device->OnResizeCreate->RemoveCallback(this.callbackIndex);
			throw new InvalidOperationException($"callback slot {this.callbackIndex} doesn't run after the render targets' ({gameIndex})");
		}
	}

	/// <summary>Takes effect on the next render target reallocation</summary>
	public bool Enabled
	{
		get => this.enabled;
		set => this.enabled = value;
	}

	/// <summary>PostTick, right after the game recreated its render targets; never fails the game's reset</summary>
	private byte OnResizeCreate(void* context)
	{
		if (!this.enabled)
			return 1;

		byte* renderTargetManager = (byte*)RenderTargetManager.Instance();
		foreach (int offset in TargetOffsets)
		{
			KernelTexture* target = *(KernelTexture**)(renderTargetManager + offset);
			try
			{
				if (target != null)
					this.TakeOver(target);
			}
			catch (Exception e)
			{
				Service.PluginLog.Error(e, "Sharing a render target failed, it keeps the game's texture");
			}
		}

		return 1;
	}

	/// <summary>Recreates the target on a source holding our shared texture, the game's texture when anything fails</summary>
	private void TakeOver(KernelTexture* target)
	{
		ID3D11Texture2D* gameTexture = (ID3D11Texture2D*)target->D3D11Texture2D;
		if (gameTexture == null || *(KernelTexture**)((byte*)target + SourceTextureOffset) != null)
			return;

		D3D11_TEXTURE2D_DESC desc = Dx.GetDesc(gameTexture);
		desc.MiscFlags |= Dx.MiscSharedNtHandle;
		ID3D11Device* device;
		gameTexture->GetDevice(&device);
		ID3D11Texture2D* shared;
		int hr = device->CreateTexture2D(&desc, null, &shared);
		device->Release();
		if (hr < 0)
		{
			Service.PluginLog.Warning($"Render target {desc.Width}x{desc.Height} {desc.Format} can't be shared (0x{hr:X8}), the AMD DLL path copies it");
			return;
		}

		// Immutable: the game creates the Kernel::Texture without a D3D11 resource, the source takes ours (and releases it)
		uint* size = stackalloc uint[2] { target->AllocatedWidth, target->AllocatedHeight };
		KernelTexture* source = Device.Instance()->CreateTexture2D((int*)size, target->MipLevel, target->TextureFormat,
			target->Flags | TextureFlags.Immutable, AllocationClass);
		if (source == null)
		{
			shared->Release();
			return;
		}

		source->D3D11Texture2D = shared;

		this.releaseResources(target);
		*(KernelTexture**)((byte*)target + SourceTextureOffset) = source;
		source->IncRef();
		bool aliased = this.resize(target, size, target->MipLevel, 0) != 0;
		source->DecRef(); // The target holds the only reference now, or none after a failed resize

		if (aliased)
			return;

		// The failed resize released the target and dropped the source, give it the game's own texture back
		Service.PluginLog.Warning($"Render target {desc.Width}x{desc.Height} didn't take the shared texture, recreating the game's");
		if (this.resize(target, size, target->MipLevel, 0) == 0)
			Service.PluginLog.Error($"Render target {desc.Width}x{desc.Height} couldn't be recreated");
	}

	public void Dispose()
	{
		this.enabled = false;
		Device.Instance()->OnResizeCreate->RemoveCallback(this.callbackIndex);
		GC.SuppressFinalize(this);
	}
}
