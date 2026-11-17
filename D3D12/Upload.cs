using System;

using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;

using static TerraFX.Interop.DirectX.D3D12_FENCE_FLAGS;
using static TerraFX.Interop.DirectX.D3D12_HEAP_FLAGS;
using static TerraFX.Interop.DirectX.D3D12_HEAP_TYPE;
using static TerraFX.Interop.DirectX.D3D12_RESOURCE_STATES;
using static TerraFX.Interop.DirectX.DirectX;
using static TerraFX.Interop.Windows.Windows;

namespace UpscaleBuddy.D3D12;

/// <summary>Each upload blocks the CPU until it is done.</summary>
public static unsafe class Upload
{
	/// <summary>In texels, x changes fastest, then y, then z. The new texture is in COMMON state.</summary>
	public static ID3D12Resource* Texture3D(ID3D12Device* device, ID3D12CommandQueue* queue, DXGI_FORMAT format, uint size, ReadOnlySpan<byte> texels,
		uint bytesPerTexel)
	{
		D3D12_RESOURCE_DESC desc = D3D12_RESOURCE_DESC.Tex3D(format, size, size, (ushort)size, 1);
		D3D12_HEAP_PROPERTIES defaultHeap = new(D3D12_HEAP_TYPE_DEFAULT);
		ID3D12Resource* texture;
		ThrowIfFailed(device->CreateCommittedResource(&defaultHeap, D3D12_HEAP_FLAG_NONE, &desc, D3D12_RESOURCE_STATE_COPY_DEST, null,
			__uuidof<ID3D12Resource>(), (void**)&texture));

		D3D12_PLACED_SUBRESOURCE_FOOTPRINT layout;
		uint rows;
		ulong rowSize, total;
		device->GetCopyableFootprints(&desc, 0, 1, 0, &layout, &rows, &rowSize, &total);
		D3D12_HEAP_PROPERTIES uploadHeap = new(D3D12_HEAP_TYPE_UPLOAD);
		D3D12_RESOURCE_DESC bufferDesc = D3D12_RESOURCE_DESC.Buffer(total);
		ID3D12Resource* buffer = null;
		ID3D12CommandAllocator* allocator = null;
		ID3D12GraphicsCommandList* list = null;
		ID3D12Fence* fence = null;
		try
		{
			ThrowIfFailed(device->CreateCommittedResource(&uploadHeap, D3D12_HEAP_FLAG_NONE, &bufferDesc, D3D12_RESOURCE_STATE_GENERIC_READ, null,
				__uuidof<ID3D12Resource>(), (void**)&buffer));
			byte* mapped;
			ThrowIfFailed(buffer->Map(0, null, (void**)&mapped));
			uint row = size * bytesPerTexel;
			for (uint z = 0; z < size; z++)
			for (uint y = 0; y < size; y++)
			{
				ReadOnlySpan<byte> source = texels.Slice((int)(((z * size) + y) * row), (int)row);
				source.CopyTo(new Span<byte>(mapped + layout.Offset + (((z * rows) + y) * layout.Footprint.RowPitch), (int)row));
			}

			buffer->Unmap(0, null);

			D3D12_COMMAND_LIST_TYPE type = queue->GetDesc().Type;
			ThrowIfFailed(device->CreateCommandAllocator(type, __uuidof<ID3D12CommandAllocator>(), (void**)&allocator));
			ThrowIfFailed(device->CreateCommandList(0, type, allocator, null, __uuidof<ID3D12GraphicsCommandList>(), (void**)&list));
			D3D12_TEXTURE_COPY_LOCATION destination = new(texture, 0);
			D3D12_TEXTURE_COPY_LOCATION from = new(buffer, layout);
			list->CopyTextureRegion(&destination, 0, 0, 0, &from, null);
			D3D12_RESOURCE_BARRIER barrier = D3D12_RESOURCE_BARRIER.InitTransition(texture, D3D12_RESOURCE_STATE_COPY_DEST, D3D12_RESOURCE_STATE_COMMON);
			list->ResourceBarrier(1, &barrier);
			ThrowIfFailed(list->Close());
			queue->ExecuteCommandLists(1, (ID3D12CommandList**)&list);
			ThrowIfFailed(device->CreateFence(0, D3D12_FENCE_FLAG_NONE, __uuidof<ID3D12Fence>(), (void**)&fence));
			ThrowIfFailed(queue->Signal(fence, 1));
			ThrowIfFailed(fence->SetEventOnCompletion(1, HANDLE.NULL));
			return texture;
		}
		catch
		{
			texture->Release();
			throw;
		}
		finally
		{
			Dx.Release(fence);
			Dx.Release(list);
			Dx.Release(allocator);
			Dx.Release(buffer);
		}
	}
}
