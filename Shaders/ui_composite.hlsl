// Draws the UI layer over the back buffer: premultiplied alpha, blended with ONE, INV_SRC_ALPHA
Texture2D<float4> ui : register(t0);

// Fullscreen triangle
float4 VS(uint id : SV_VertexID) : SV_Position
{
	float2 uv = float2((id << 1) & 2, id & 2);
	return float4(uv * float2(2, -2) + float2(-1, 1), 0, 1);
}

float4 PS(float4 position : SV_Position) : SV_Target
{
	return ui.Load(int3(position.xy, 0));
}
