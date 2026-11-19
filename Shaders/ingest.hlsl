#include "common.hlsli"

// YUV to RGB conversion for the capture formats. Matrix rows and range come from the CPU.
cbuffer IngestCB : register(b0)
{
    float4 RowR;      // coefficients for (y, u, v) and unused
    float4 RowG;
    float4 RowB;
    float4 Range;     // y offset, y scale, c offset, c scale
    float4 Size;      // width, height, 1/width, 1/height
};

Texture2D<float> LumaTex : register(t0);
Texture2D<float2> ChromaTex : register(t1);
Texture2D<float4> PackedTex : register(t2);

float3 ToRgb(float y, float u, float v)
{
    y = (y - Range.x) * Range.y;
    u = (u - Range.z) * Range.w;
    v = (v - Range.z) * Range.w;
    float3 yuv = float3(y, u, v);
    return saturate(float3(dot(RowR.xyz, yuv), dot(RowG.xyz, yuv), dot(RowB.xyz, yuv)));
}

float4 PSNv12(VSOut i) : SV_Target
{
    float y = LumaTex.Sample(PointClamp, i.uv);
    // 4:2:0 chroma is sited left of the luma pair: shift half a luma pixel.
    float2 c = ChromaTex.Sample(LinearClamp, i.uv + float2(0.5 * Size.z, 0));
    return float4(ToRgb(y, c.x, c.y), 1);
}

float4 PSYuy2(VSOut i) : SV_Target
{
    uint2 p = uint2(i.pos.xy);
    float4 t = PackedTex.Load(int3(p.x >> 1, p.y, 0));
    float y = (p.x & 1) ? t.b : t.r;
    return float4(ToRgb(y, t.g, t.a), 1);
}

float4 PSBgra(VSOut i) : SV_Target
{
    return float4(PackedTex.Sample(PointClamp, i.uv).rgb, 1);
}
