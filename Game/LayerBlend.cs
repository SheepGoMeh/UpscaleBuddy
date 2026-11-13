using System;

using TerraFX.Interop.DirectX;

using static TerraFX.Interop.DirectX.D3D11_BLEND;
using static TerraFX.Interop.DirectX.D3D11_BLEND_OP;
using static TerraFX.Interop.Windows.Windows;

namespace UpscaleBuddy.Game;

/// <summary>
/// Premultiplied alpha blend in a separate device context state. Leaves the game's bindings and state cache unchanged.
/// Not thread safe: one context state must not be active on two threads.
/// </summary>
public sealed unsafe class LayerBlend: IDisposable
{
	private ID3D11DeviceContext1* context1;
	private ID3DDeviceContextState* state;
	private ID3D11VertexShader* vertexShader;
	private ID3D11PixelShader* pixelShader;
	private ID3D11BlendState* blendState;

	public void Draw(ID3D11DeviceContext* context, ID3D11RenderTargetView* target, uint width, uint height,
		ID3D11ShaderResourceView* source, bool blend)
	{
		if (this.state == null)
			this.CreateResources(context);

		ID3DDeviceContextState* previous;
		this.context1->SwapDeviceContextState(this.state, &previous);

		D3D11_VIEWPORT viewport = new() { Width = width, Height = height, MaxDepth = 1 };
		context->OMSetRenderTargets(1, &target, null);
		context->OMSetBlendState(blend ? this.blendState : null, null, uint.MaxValue);
		context->RSSetViewports(1, &viewport);
		context->IASetPrimitiveTopology(D3D_PRIMITIVE_TOPOLOGY.D3D_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
		context->VSSetShader(this.vertexShader, null, 0);
		context->PSSetShader(this.pixelShader, null, 0);
		context->PSSetShaderResources(0, 1, &source);
		context->Draw(3, 0);

		// An inactive state that holds bindings would keep references to the targets, also through ResizeBuffers
		ID3D11ShaderResourceView* noView = null;
		context->PSSetShaderResources(0, 1, &noView);
		context->OMSetRenderTargets(0, null, null);

		this.context1->SwapDeviceContextState(previous, null);
		previous->Release();
	}

	private void CreateResources(ID3D11DeviceContext* context)
	{
		ID3D11Device* device;
		context->GetDevice(&device);
		try
		{
			this.context1 = Dx.Query<ID3D11DeviceContext1>(context);
			ID3D11Device1* device1 = Dx.Query<ID3D11Device1>(device);
			D3D_FEATURE_LEVEL level = device->GetFeatureLevel();
			ID3DDeviceContextState* created;
			// The state must match a single threaded device
			uint flags = (device->GetCreationFlags() & (uint)D3D11_CREATE_DEVICE_FLAG.D3D11_CREATE_DEVICE_SINGLETHREADED) != 0
				? (uint)D3D11_1_CREATE_DEVICE_CONTEXT_STATE_FLAG.D3D11_1_CREATE_DEVICE_CONTEXT_STATE_SINGLETHREADED
				: 0;
			int hr = device1->CreateDeviceContextState(flags, &level, 1, D3D11.D3D11_SDK_VERSION, __uuidof<ID3D11Device>(), null, &created);
			device1->Release();
			ThrowIfFailed(hr);

			this.vertexShader = Dx.CreateVertexShader(device, "Shaders.ui_composite_vs.cso");
			this.pixelShader = Dx.CreatePixelShader(device, "Shaders.ui_composite_ps.cso");

			D3D11_BLEND_DESC desc = default;
			desc.RenderTarget[0] = new D3D11_RENDER_TARGET_BLEND_DESC
			{
				BlendEnable = true, SrcBlend = D3D11_BLEND_ONE, DestBlend = D3D11_BLEND_INV_SRC_ALPHA, BlendOp = D3D11_BLEND_OP_ADD,
				SrcBlendAlpha = D3D11_BLEND_ONE, DestBlendAlpha = D3D11_BLEND_INV_SRC_ALPHA, BlendOpAlpha = D3D11_BLEND_OP_ADD,
				RenderTargetWriteMask = (byte)D3D11_COLOR_WRITE_ENABLE.D3D11_COLOR_WRITE_ENABLE_ALL,
			};
			ID3D11BlendState* blend;
			ThrowIfFailed(device->CreateBlendState(&desc, &blend));
			this.blendState = blend;
			this.state = created;
		}
		finally
		{
			device->Release();
		}
	}

	public void Dispose()
	{
		Dx.Release(this.blendState);
		Dx.Release(this.pixelShader);
		Dx.Release(this.vertexShader);
		Dx.Release(this.state);
		Dx.Release(this.context1);
		this.blendState = null;
		this.pixelShader = null;
		this.vertexShader = null;
		this.state = null;
		this.context1 = null;
	}
}
