// Supersampled color to the output size, four bilinear taps per output pixel

Texture2D<float4> Source : register(t0);
RWTexture2D<float4> Output : register(u0);
SamplerState LinearClamp : register(s1);

cbuffer Constants : register(b0)
{
	float2 SourceScale; // render size / source texture size
	float2 OutputSizeInv;
	uint2 OutputSize;
	uint2 Padding;
};

[numthreads(8, 8, 1)]
void CS(uint2 id : SV_DispatchThreadID)
{
	if (any(id >= OutputSize))
		return;

	float2 uv = (id + 0.5f) * OutputSizeInv * SourceScale;
	float2 offset = 0.25f * OutputSizeInv * SourceScale;
	float4 color = Source.SampleLevel(LinearClamp, uv + float2(-offset.x, -offset.y), 0);
	color += Source.SampleLevel(LinearClamp, uv + float2(offset.x, -offset.y), 0);
	color += Source.SampleLevel(LinearClamp, uv + float2(-offset.x, offset.y), 0);
	color += Source.SampleLevel(LinearClamp, uv + float2(offset.x, offset.y), 0);
	Output[id] = color * 0.25f;
}
