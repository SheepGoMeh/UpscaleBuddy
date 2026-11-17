using System;

using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;

using static TerraFX.Interop.DirectX.D3D_ROOT_SIGNATURE_VERSION;
using static TerraFX.Interop.DirectX.D3D12_COMPARISON_FUNC;
using static TerraFX.Interop.DirectX.D3D12_DESCRIPTOR_HEAP_FLAGS;
using static TerraFX.Interop.DirectX.D3D12_DESCRIPTOR_HEAP_TYPE;
using static TerraFX.Interop.DirectX.D3D12_DESCRIPTOR_RANGE_TYPE;
using static TerraFX.Interop.DirectX.D3D12_FILTER;
using static TerraFX.Interop.DirectX.D3D12_SHADER_VISIBILITY;
using static TerraFX.Interop.DirectX.D3D12_SRV_DIMENSION;
using static TerraFX.Interop.DirectX.D3D12_TEXTURE_ADDRESS_MODE;
using static TerraFX.Interop.DirectX.D3D12_UAV_DIMENSION;
using static TerraFX.Interop.DirectX.DirectX;
using static TerraFX.Interop.DirectX.DXGI_FORMAT;
using static TerraFX.Interop.Windows.Windows;

namespace UpscaleBuddy.D3D12;

/// <summary>
/// All textures are in COMMON state before and after the pass. Ui and Lut can be null.
/// </summary>
public readonly unsafe struct FinishResources(ID3D12Resource* scene, ID3D12Resource* ui, ID3D12Resource* lut, ID3D12Resource* blendLut,
	ID3D12Resource* output)
{
	public readonly ID3D12Resource* Scene = scene; // B8G8R8A8_UNORM
	public readonly ID3D12Resource* Ui = ui; // B8G8R8A8_UNORM, premultiplied
	public readonly ID3D12Resource* Lut = lut; // 16^3 colour filter LUT
	public readonly ID3D12Resource* BlendLut = blendLut; // 256^3 R8_UINT, from FinishCalibration
	public readonly ID3D12Resource* Output = output; // R8G8B8A8_TYPELESS with UAV access
}

/// <summary>The cbuffer layout of Shaders/finish.hlsl.</summary>
public struct FinishConstants
{
	public const uint ToneAdjust = 1;
	public const uint UiDrawn = 2;

	public float Exponent;
	public float Strength;
	public uint Flags;
}

/// <summary>Runs Shaders/finish.hlsl. Each slot has its own descriptor set.</summary>
public sealed unsafe class FinishPass: IDisposable
{
	private const uint DescriptorsPerSlot = 5; // t0-t3, u0

	private readonly ID3D12Device* device;
	private readonly ID3D12RootSignature* rootSignature;
	private readonly ID3D12PipelineState* pipeline;
	private readonly ID3D12DescriptorHeap* heap;
	private readonly uint descriptorSize;

	public FinishPass(ID3D12Device* device, int slots)
	{
		this.device = device;
		device->AddRef();
		try
		{
			D3D12_DESCRIPTOR_RANGE* ranges = stackalloc D3D12_DESCRIPTOR_RANGE[2];
			ranges[0] = new D3D12_DESCRIPTOR_RANGE(D3D12_DESCRIPTOR_RANGE_TYPE_SRV, 4, 0);
			ranges[1] = new D3D12_DESCRIPTOR_RANGE(D3D12_DESCRIPTOR_RANGE_TYPE_UAV, 1, 0);
			D3D12_ROOT_PARAMETER* parameters = stackalloc D3D12_ROOT_PARAMETER[3];
			D3D12_ROOT_PARAMETER.InitAsConstants(ref parameters[0], 4, 0);
			D3D12_ROOT_PARAMETER.InitAsDescriptorTable(ref parameters[1], 1, &ranges[0]);
			D3D12_ROOT_PARAMETER.InitAsDescriptorTable(ref parameters[2], 1, &ranges[1]);

			// The game's sLUT sampler (key 618) uses MIN_MAG_LINEAR_MIP_POINT, clamp, and LOD 0 to 0.
			D3D12_STATIC_SAMPLER_DESC sampler = new()
			{
				Filter = D3D12_FILTER_MIN_MAG_LINEAR_MIP_POINT, AddressU = D3D12_TEXTURE_ADDRESS_MODE_CLAMP, AddressV = D3D12_TEXTURE_ADDRESS_MODE_CLAMP,
				AddressW = D3D12_TEXTURE_ADDRESS_MODE_CLAMP, MaxAnisotropy = 1, ComparisonFunc = D3D12_COMPARISON_FUNC_NEVER, MinLOD = 0, MaxLOD = 0,
				ShaderRegister = 0, ShaderVisibility = D3D12_SHADER_VISIBILITY_ALL,
			};
			D3D12_ROOT_SIGNATURE_DESC rootDesc = new(3, parameters, 1, &sampler);
			ID3DBlob* serialized;
			ID3DBlob* errors = null;
			HRESULT result = D3D12SerializeRootSignature(&rootDesc, D3D_ROOT_SIGNATURE_VERSION_1, &serialized, &errors);
			Dx.Release(errors);
			ThrowIfFailed(result);
			ID3D12RootSignature* createdRoot;
			result = device->CreateRootSignature(0, serialized->GetBufferPointer(), serialized->GetBufferSize(), __uuidof<ID3D12RootSignature>(),
				(void**)&createdRoot);
			serialized->Release();
			ThrowIfFailed(result);
			this.rootSignature = createdRoot;

			byte[] code = Dx.ReadResource("Shaders.finish.cso");
			ID3D12PipelineState* createdPipeline;
			fixed (byte* bytes = code)
			{
				D3D12_COMPUTE_PIPELINE_STATE_DESC pipelineDesc = new()
				{
					pRootSignature = this.rootSignature, CS = new D3D12_SHADER_BYTECODE(bytes, (nuint)code.Length),
				};
				ThrowIfFailed(device->CreateComputePipelineState(&pipelineDesc, __uuidof<ID3D12PipelineState>(), (void**)&createdPipeline));
			}

			this.pipeline = createdPipeline;

			D3D12_DESCRIPTOR_HEAP_DESC heapDesc = new()
			{
				Type = D3D12_DESCRIPTOR_HEAP_TYPE_CBV_SRV_UAV, NumDescriptors = DescriptorsPerSlot * (uint)slots,
				Flags = D3D12_DESCRIPTOR_HEAP_FLAG_SHADER_VISIBLE,
			};
			ID3D12DescriptorHeap* createdHeap;
			ThrowIfFailed(device->CreateDescriptorHeap(&heapDesc, __uuidof<ID3D12DescriptorHeap>(), (void**)&createdHeap));
			this.heap = createdHeap;
			this.descriptorSize = device->GetDescriptorHandleIncrementSize(D3D12_DESCRIPTOR_HEAP_TYPE_CBV_SRV_UAV);
		}
		catch
		{
			this.Dispose();
			throw;
		}
	}

	/// <summary>The GPU must be done with the slot's previous pass.</summary>
	public void Record(ID3D12GraphicsCommandList* list, int slot, in FinishResources resources, in FinishConstants constants)
	{
		uint first = (uint)slot * DescriptorsPerSlot;
		this.Srv(first, resources.Scene, D3D12_SRV_DIMENSION_TEXTURE2D, DXGI_FORMAT_B8G8R8A8_UNORM);
		this.Srv(first + 1, resources.Ui, D3D12_SRV_DIMENSION_TEXTURE2D, DXGI_FORMAT_B8G8R8A8_UNORM);
		this.Srv(first + 2, resources.Lut, D3D12_SRV_DIMENSION_TEXTURE3D, DXGI_FORMAT_B8G8R8X8_UNORM);
		this.Srv(first + 3, resources.BlendLut, D3D12_SRV_DIMENSION_TEXTURE3D, DXGI_FORMAT_R8_UINT);
		D3D12_UNORDERED_ACCESS_VIEW_DESC uavDesc = new() { Format = DXGI_FORMAT_R8G8B8A8_UINT, ViewDimension = D3D12_UAV_DIMENSION_TEXTURE2D };
		this.device->CreateUnorderedAccessView(resources.Output, null, &uavDesc, this.Cpu(first + 4));

		ID3D12DescriptorHeap* heap = this.heap;
		list->SetDescriptorHeaps(1, &heap);
		list->SetComputeRootSignature(this.rootSignature);
		list->SetPipelineState(this.pipeline);
		uint* values = stackalloc uint[4] { BitConverter.SingleToUInt32Bits(constants.Exponent), BitConverter.SingleToUInt32Bits(constants.Strength),
			constants.Flags, 0 };
		list->SetComputeRoot32BitConstants(0, 4, values, 0);
		list->SetComputeRootDescriptorTable(1, this.Gpu(first));
		list->SetComputeRootDescriptorTable(2, this.Gpu(first + 4));
		D3D12_RESOURCE_DESC desc = resources.Output->GetDesc();
		list->Dispatch((uint)((desc.Width + 7) / 8), (desc.Height + 7) / 8, 1);
	}

	/// <summary>A null resource gets a null view. A null view reads 0.</summary>
	private void Srv(uint index, ID3D12Resource* resource, D3D12_SRV_DIMENSION dimension, DXGI_FORMAT nullFormat)
	{
		D3D12_SHADER_RESOURCE_VIEW_DESC desc = new()
		{
			Format = resource == null ? nullFormat : resource->GetDesc().Format, ViewDimension = dimension,
			Shader4ComponentMapping = TerraFX.Interop.DirectX.D3D12.D3D12_DEFAULT_SHADER_4_COMPONENT_MAPPING,
		};
		if (dimension == D3D12_SRV_DIMENSION_TEXTURE3D)
			desc.Texture3D.MipLevels = 1;
		else
			desc.Texture2D.MipLevels = 1;
		this.device->CreateShaderResourceView(resource, &desc, this.Cpu(index));
	}

	private D3D12_CPU_DESCRIPTOR_HANDLE Cpu(uint index) =>
		new() { ptr = this.heap->GetCPUDescriptorHandleForHeapStart().ptr + (index * this.descriptorSize) };

	private D3D12_GPU_DESCRIPTOR_HANDLE Gpu(uint index) =>
		new() { ptr = this.heap->GetGPUDescriptorHandleForHeapStart().ptr + (index * this.descriptorSize) };

	public void Dispose()
	{
		Dx.Release(this.heap);
		Dx.Release(this.pipeline);
		Dx.Release(this.rootSignature);
		Dx.Release(this.device);
	}
}
