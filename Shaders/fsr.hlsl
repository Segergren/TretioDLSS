// FSR 1: EASU upscale and RCAS sharpen, as pixel shaders.
#include "common.hlsli"

#define A_GPU 1
#define A_HLSL 1
#include "ffx/ffx_a.h"

cbuffer FsrCB : register(b0)
{
    uint4 Con0;
    uint4 Con1;
    uint4 Con2;
    uint4 Con3;
    int4 Origin;    // pixel offset of the output inside the render target
};

Texture2D<float4> Src : register(t0);

#define FSR_EASU_F 1
AF4 FsrEasuRF(AF2 p) { return Src.GatherRed(LinearClamp, p); }
AF4 FsrEasuGF(AF2 p) { return Src.GatherGreen(LinearClamp, p); }
AF4 FsrEasuBF(AF2 p) { return Src.GatherBlue(LinearClamp, p); }

#define FSR_RCAS_F 1
AF4 FsrRcasLoadF(ASU2 p) { return Src.Load(int3(p, 0)); }
void FsrRcasInputF(inout AF1 r, inout AF1 g, inout AF1 b) { }

#include "ffx/ffx_fsr1.h"

float4 PSEasu(VSOut i) : SV_Target
{
    AF3 c;
    FsrEasuF(c, AU2(i.pos.xy) - AU2(Origin.xy), Con0, Con1, Con2, Con3);
    return float4(saturate(c), 1);
}

float4 PSRcas(VSOut i) : SV_Target
{
    AF3 c;
    FsrRcasF(c.r, c.g, c.b, AU2(i.pos.xy) - AU2(Origin.xy), Con0);
    return float4(saturate(c), 1);
}
