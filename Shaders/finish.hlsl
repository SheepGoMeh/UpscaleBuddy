// The output is bit exact with LayerBlend followed by ToneAdjust (shader/sm5/posteffect/ToneAdjust.shcd).

cbuffer Finish : register(b0)
{
	float exponent; // ToneAdjust cb0[0].w
	float strength; // ToneAdjust cb0[0].z
	uint flags;
	uint pad;
};

static const uint ToneAdjust = 1;
static const uint UiDrawn = 2;

Texture2D<float4> scene : register(t0);
Texture2D<float4> ui : register(t1); // premultiplied
Texture3D<float4> lut : register(t2);
Texture3D<uint> blend : register(t3); // x = destination, y = source, z = source alpha
SamplerState lutSampler : register(s0);
RWTexture2D<uint4> output : register(u0); // R8G8B8A8_UINT view

// Emulates the float -> UNORM8 conversion of a render target. D3D allows 0.6 ULP, so FinishCalibration checks this
// function on each device.
uint4 Unorm8(float4 value)
{
	float4 half = asfloat(asuint(saturate(value)) & 0xFFFFE000u);
	return (uint4)floor(half * 255.0 + 0.5);
}

[numthreads(8, 8, 1)]
void CS(uint3 id : SV_DispatchThreadID)
{
	uint width, height;
	output.GetDimensions(width, height);
	if (id.x >= width || id.y >= height)
		return;

	uint4 d = (uint4)round(scene[id.xy] * 255.0);
	float4 c = d / 255.0;
	if (flags & UiDrawn)
	{
		// LayerBlend uses ONE / INV_SRC_ALPHA.
		uint4 s = (uint4)round(ui[id.xy] * 255.0);
		c = float4(blend[uint3(d.r, s.r, s.a)], blend[uint3(d.g, s.g, s.a)], blend[uint3(d.b, s.b, s.a)], blend[uint3(d.a, s.a, s.a)]) / 255.0;
	}

	if (flags & ToneAdjust)
	{
		c.rgb = saturate(c.rgb);
		c.rgb = exp2(log2(c.rgb) * exponent);
		if (0 < strength)
		{
			float3 graded = lut.SampleLevel(lutSampler, c.rgb * 0.9375 + 0.03125, 0).rgb;
			c.rgb = c.rgb + strength * (graded - c.rgb);
		}
	}

	output[id.xy] = Unorm8(c);
}
