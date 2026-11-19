#include "common.hlsli"

// Present copy: the source texture is drawn 1:1 into a viewport of the back buffer.
cbuffer CopyCB : register(b0)
{
    int4 CopyOrigin;   // viewport origin in pixels
};

Texture2D<float4> Src : register(t0);
Texture2D<float4> SrcB : register(t1);

float4 PSCopy(VSOut i) : SV_Target
{
    return float4(Src.Load(int3(int2(i.pos.xy) - CopyOrigin.xy, 0)).rgb, 1);
}

// Supersample present: each output pixel is the average of a 2 x 2 source block.
float4 PSDown2(VSOut i) : SV_Target
{
    int2 p = (int2(i.pos.xy) - CopyOrigin.xy) * 2;
    float3 c = Src.Load(int3(p, 0)).rgb + Src.Load(int3(p + int2(1, 0), 0)).rgb
             + Src.Load(int3(p + int2(0, 1), 0)).rgb + Src.Load(int3(p + int2(1, 1), 0)).rgb;
    return float4(c * 0.25, 1);
}

// Block luma difference between two frames into a 16 x 9 target, sampling an 8 x 8 grid per block.
cbuffer DiffCB : register(b1)
{
    float4 DiffSize;   // source width, height
};

float Luma(float3 c) { return dot(c, float3(0.299, 0.587, 0.114)); }

float PSDiff(VSOut i) : SV_Target
{
    uint2 block = uint2(i.pos.xy);
    float2 blockSize = DiffSize.xy / float2(16, 9);
    float sum = 0;
    [loop] for (int y = 0; y < 8; y++)
    [loop] for (int x = 0; x < 8; x++)
    {
        int2 p = int2((block + (float2(x, y) + 0.5) / 8.0) * blockSize);
        sum += abs(Luma(Src.Load(int3(p, 0)).rgb) - Luma(SrcB.Load(int3(p, 0)).rgb));
    }
    return sum / 64.0 * 255.0;
}

// GPU-drawn test pattern (the BGRA source path). Same layout as the CPU pattern.
cbuffer PatternCB : register(b2)
{
    float4 PatternInfo;   // content index, width, height, bar step in pixels
};

float4 PSPattern(VSOut i) : SV_Target
{
    float2 p = i.pos.xy;
    float w = PatternInfo.y, h = PatternInfo.z;
    uint index = (uint)PatternInfo.x;
    // detailed background
    float checker = ((uint(p.x / 16) + uint(p.y / 16)) & 1) ? 0.30 : 0.22;
    float3 c = float3(checker * 0.8, checker * 0.6, checker);
    c += 0.08 * sin(p.x * 0.11) * sin(p.y * 0.07);
    // binary index strip: 24 blocks of 32 x 32 at the top-left
    if (p.y < 32 && p.x < 24 * 32)
    {
        uint bit = 23 - uint(p.x / 32);
        c = ((index >> bit) & 1) ? float3(1, 1, 1) : float3(0, 0, 0);
    }
    // textured bar moving with the index
    float barX = fmod(index * PatternInfo.w, w - 64);
    if (p.y > 120 && p.y < h - 60 && p.x >= barX && p.x < barX + 64)
    {
        float stripe = (uint((p.y - 120) / 8) & 1) ? 1.0 : 0.75;
        c = float3(1.0, 0.55, 0.2) * stripe;
    }
    return float4(saturate(c), 1);
}

// SDR to HDR (scRGB, linear, 1.0 = 80 nits): the composed SDR picture is decoded to linear light,
// placed at paper white, and highlights are lifted by Expand.
cbuffer HdrCB : register(b0)
{
    float4 HdrA;   // paper white / 80, expand, brightness, contrast
    float4 HdrB;   // saturation
};

float3 SrgbToLinear(float3 c)
{
    return c <= 0.04045 ? c / 12.92 : pow((c + 0.055) / 1.055, 2.4);
}

float4 PSHdr(VSOut i) : SV_Target
{
    float3 c = Src.Load(int3(int2(i.pos.xy), 0)).rgb;
    c = saturate((c - 0.5) * HdrA.w + 0.5 + HdrA.z);
    float3 lin = SrgbToLinear(c);
    float luma = dot(lin, float3(0.2126, 0.7152, 0.0722));
    lin = max(0, lerp(luma.xxx, lin, HdrB.x));
    lin *= 1 + HdrA.y * luma * luma;
    return float4(lin * HdrA.x, 1);
}
