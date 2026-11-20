#include "common.hlsli"

// RGB to NV12 for the stream encoder: BT.709, limited range, chroma averaged over 2 x 2.
Texture2D<float4> Src : register(t0);

static const float3 KY = float3(0.2126, 0.7152, 0.0722);
static const float3 KU = float3(-0.1146, -0.3854, 0.5);
static const float3 KV = float3(0.5, -0.4542, -0.0458);

float PSLuma(VSOut i) : SV_Target
{
    float3 c = Src.Load(int3(int2(i.pos.xy), 0)).rgb;
    return (16.0 + 219.0 * dot(c, KY)) / 255.0;
}

float2 PSChroma(VSOut i) : SV_Target
{
    int2 p = int2(i.pos.xy) * 2;
    float3 c = Src.Load(int3(p, 0)).rgb + Src.Load(int3(p + int2(1, 0), 0)).rgb
             + Src.Load(int3(p + int2(0, 1), 0)).rgb + Src.Load(int3(p + int2(1, 1), 0)).rgb;
    c *= 0.25;
    return (128.0 + 224.0 * float2(dot(c, KU), dot(c, KV))) / 255.0;
}
