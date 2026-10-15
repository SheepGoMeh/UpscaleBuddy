// The game's ToneAdjust without a colour filter: the exponent is Device.GammaCorrection, applied to the clamped colour, the alpha
// stays; no sRGB conversion and no dithering. Reads the composed frame, so the pass can't write into it.
Texture2D<float4> source : register(t0);

cbuffer Tone : register(b0)
{
	float gamma;
	float3 padding;
};

float4 PS(float4 position : SV_Position) : SV_Target
{
	float4 color = source.Load(int3(position.xy, 0));
	return float4(pow(saturate(color.rgb), gamma), color.a);
}
