// Rain and VFX coverage (alpha of the game's depth of field effect mask, Game/EffectMask.cs) to the upscalers' reactive mask

Texture2D<float4> Mask : register(t0);
RWTexture2D<float> Reactive : register(u0);

cbuffer Constants : register(b0)
{
	float Scale;
	float3 Padding;
};

[numthreads(8, 8, 1)]
void CS(uint2 id : SV_DispatchThreadID)
{
	Reactive[id] = saturate(Mask.Load(int3(id, 0)).a * Scale);
}
