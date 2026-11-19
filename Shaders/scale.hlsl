#include "common.hlsli"

cbuffer ScaleCB : register(b0)
{
    float4 SrcSize;   // width, height, 1/width, 1/height
    float4 SrcRect;   // uv offset xy, uv scale zw
};

Texture2D<float4> Src : register(t0);

float2 SrcUv(float2 uv) { return SrcRect.xy + uv * SrcRect.zw; }

float4 PSBilinear(VSOut i) : SV_Target
{
    return float4(Src.Sample(LinearClamp, SrcUv(i.uv)).rgb, 1);
}

float4 PSPoint(VSOut i) : SV_Target
{
    return float4(Src.Sample(PointClamp, SrcUv(i.uv)).rgb, 1);
}

// Catmull-Rom with 9 bilinear taps (the usual trick that folds the 16 taps).
float4 PSBicubic(VSOut i) : SV_Target
{
    float2 pos = SrcUv(i.uv) * SrcSize.xy;
    float2 center = floor(pos - 0.5) + 0.5;
    float2 f = pos - center;
    float2 w0 = f * (-0.5 + f * (1.0 - 0.5 * f));
    float2 w1 = 1.0 + f * f * (-2.5 + 1.5 * f);
    float2 w2 = f * (0.5 + f * (2.0 - 1.5 * f));
    float2 w3 = f * f * (-0.5 + 0.5 * f);
    float2 w12 = w1 + w2;
    float2 off12 = w2 / w12;
    float2 t0 = (center - 1) * SrcSize.zw;
    float2 t3 = (center + 2) * SrcSize.zw;
    float2 t12 = (center + off12) * SrcSize.zw;
    float3 c = 0;
    c += Src.SampleLevel(LinearClamp, float2(t0.x, t0.y), 0).rgb * w0.x * w0.y;
    c += Src.SampleLevel(LinearClamp, float2(t12.x, t0.y), 0).rgb * w12.x * w0.y;
    c += Src.SampleLevel(LinearClamp, float2(t3.x, t0.y), 0).rgb * w3.x * w0.y;
    c += Src.SampleLevel(LinearClamp, float2(t0.x, t12.y), 0).rgb * w0.x * w12.y;
    c += Src.SampleLevel(LinearClamp, float2(t12.x, t12.y), 0).rgb * w12.x * w12.y;
    c += Src.SampleLevel(LinearClamp, float2(t3.x, t12.y), 0).rgb * w3.x * w12.y;
    c += Src.SampleLevel(LinearClamp, float2(t0.x, t3.y), 0).rgb * w0.x * w3.y;
    c += Src.SampleLevel(LinearClamp, float2(t12.x, t3.y), 0).rgb * w12.x * w3.y;
    c += Src.SampleLevel(LinearClamp, float2(t3.x, t3.y), 0).rgb * w3.x * w3.y;
    return float4(saturate(c), 1);
}

// Linear blend between two frames; used by the debug frame generator.
Texture2D<float4> SrcB : register(t1);
cbuffer BlendCB : register(b1)
{
    float4 BlendT;   // x = t
};

float4 PSBlend(VSOut i) : SV_Target
{
    float3 a = Src.Sample(PointClamp, i.uv).rgb;
    float3 b = SrcB.Sample(PointClamp, i.uv).rgb;
    return float4(lerp(a, b, BlendT.x), 1);
}
