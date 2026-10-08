// Game depth (R24G8, not shareable) to an R32_FLOAT twin that D3D12 can open

Texture2D<float> Source : register(t0);
RWTexture2D<float> Output : register(u0);

[numthreads(8, 8, 1)]
void CS(uint2 id : SV_DispatchThreadID)
{
	Output[id] = Source.Load(int3(id, 0));
}
