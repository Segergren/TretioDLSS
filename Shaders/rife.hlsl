// RIFE warp and blend at full resolution (PLAN phase 10). The model ran on frames scaled to its
// own size and returned, per half-resolution pixel, two texels of RGBA16F: (motion to frame 0,
// motion to frame 1) in model pixels, then (mask, 0, 0, 0).

#include "common.hlsli"

Texture2D<float4> Frame0 : register(t0);
Texture2D<float4> Frame1 : register(t1);
Texture2D<float4> Motion : register(t2);

cbuffer RifeConstants : register(b0)
{
    float2 ModelSize;  // model input size in pixels
    float2 FlowSize;   // half of it
    float T;           // position of the generated frame, 0..1
    float3 Pad;
};

void LoadFlow(int2 p, out float4 flow, out float mask)
{
    p = clamp(p, int2(0, 0), int2(FlowSize) - 1);
    flow = Motion.Load(int3(p.x * 2, p.y, 0));
    mask = Motion.Load(int3(p.x * 2 + 1, p.y, 0)).x;
}

float4 PSRifeWarp(VSOut i) : SV_Target
{
    // Bilinear between the four nearest motion samples.
    float2 f = i.uv * FlowSize - 0.5;
    int2 p = int2(floor(f));
    float2 w = f - p;
    float4 f00, f10, f01, f11;
    float m00, m10, m01, m11;
    LoadFlow(p, f00, m00);
    LoadFlow(p + int2(1, 0), f10, m10);
    LoadFlow(p + int2(0, 1), f01, m01);
    LoadFlow(p + int2(1, 1), f11, m11);
    float4 flow = lerp(lerp(f00, f10, w.x), lerp(f01, f11, w.x), w.y);
    float mask = lerp(lerp(m00, m10, w.x), lerp(m01, m11, w.x), w.y);

    // The model estimated the middle frame; other positions scale the motion linearly.
    float2 to0 = flow.xy * (2.0 * T) / ModelSize;
    float2 to1 = flow.zw * (2.0 * (1.0 - T)) / ModelSize;
    float3 c0 = Frame0.SampleLevel(LinearClamp, i.uv + to0, 0).rgb;
    float3 c1 = Frame1.SampleLevel(LinearClamp, i.uv + to1, 0).rgb;
    float w0 = mask * (1.0 - T);
    float w1 = (1.0 - mask) * T;
    return float4((c0 * w0 + c1 * w1) / max(w0 + w1, 1e-4), 1);
}
