using System.Runtime.InteropServices;
using TretioDLSS.Graphics;
using TretioDLSS.Pipeline;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace TretioDLSS.Engines;

[StructLayout(LayoutKind.Sequential)]
internal struct NisConstants
{
    public float DetectRatio, DetectThres, MinContrastRatio, RatioNorm;
    public float ContrastBoost, Eps, SharpStartY, SharpScaleY;
    public float SharpStrengthMin, SharpStrengthScale, SharpLimitMin, SharpLimitScale;
    public float ScaleX, ScaleY, DstNormX, DstNormY;
    public float SrcNormX, SrcNormY;
    public uint InOriginX, InOriginY, InWidth, InHeight;
    public uint OutOriginX, OutOriginY, OutWidth, OutHeight;
    public float Reserved0, Reserved1;
}

/// <summary>NVIDIA Image Scaling (NVScaler compute pass), for scale factors 1 to 2.</summary>
public sealed class NisUpscaler : IUpscaler
{
    private const string Defines = "#define NIS_BLOCK_WIDTH 32\n#define NIS_BLOCK_HEIGHT 24\n#define NIS_THREAD_GROUP_SIZE 128\n#define NIS_SCALER 1\n#define NIS_HDR_MODE 0\n";
    private readonly GraphicsDevice _g;
    private readonly ID3D11Buffer _cb;
    private readonly ID3D11Texture2D _coefScale, _coefUsm;
    private readonly ID3D11ShaderResourceView _coefScaleSrv, _coefUsmSrv;
    private ID3D11Texture2D? _uavTex;
    private ID3D11UnorderedAccessView? _uav;
    private readonly ShaderUpscaler _fallback;

    public string Id => "nis";
    public string Status { get; private set; } = "";

    public NisUpscaler(GraphicsDevice g)
    {
        _g = g;
        _cb = g.CreateConstantBuffer(Marshal.SizeOf<NisConstants>());
        (_coefScale, _coefScaleSrv) = Coefficients(NisCoefficients.Scale);
        (_coefUsm, _coefUsmSrv) = Coefficients(NisCoefficients.Usm);
        _fallback = new ShaderUpscaler(g, "bicubic");
        g.ComputeShader("nis/NIS_Main.hlsl", "main", Defines);
    }

    private (ID3D11Texture2D, ID3D11ShaderResourceView) Coefficients(float[] values)
    {
        var tex = _g.Device.CreateTexture2D(values.AsSpan(), Format.R32G32B32A32_Float, 2, 64, 1, 1, BindFlags.ShaderResource, ResourceOptionFlags.None, ResourceUsage.Immutable, CpuAccessFlags.None);
        return (tex, _g.Device.CreateShaderResourceView(tex));
    }

    internal static NisConstants Constants(float sharpness, int inW, int inH, int outW, int outH)
    {
        sharpness = Math.Clamp(sharpness, 0, 1);
        float slider = sharpness - 0.5f;
        float maxScale = slider >= 0 ? 1.25f : 1.75f;
        float minScale = slider >= 0 ? 1.25f : 1.0f;
        float limitScale = slider >= 0 ? 1.25f : 1.0f;
        float minContrast = 2.0f, maxContrast = 10.0f, startY = 0.45f, endY = 0.9f;
        float strengthMin = Math.Max(0, 0.4f + slider * minScale * 1.2f);
        float strengthMax = 1.6f + slider * maxScale * 1.8f;
        float limitMin = Math.Max(0.1f, 0.14f + slider * limitScale * 0.32f);
        float limitMax = 0.5f + slider * limitScale * 0.6f;
        return new NisConstants
        {
            DetectRatio = 2 * 1127f / 1024f,
            DetectThres = 64f / 1024f,
            MinContrastRatio = minContrast,
            RatioNorm = 1f / (maxContrast - minContrast),
            ContrastBoost = 1f,
            Eps = 1f / 255f,
            SharpStartY = startY,
            SharpScaleY = 1f / (endY - startY),
            SharpStrengthMin = strengthMin,
            SharpStrengthScale = strengthMax - strengthMin,
            SharpLimitMin = limitMin,
            SharpLimitScale = limitMax - limitMin,
            ScaleX = inW / (float)outW,
            ScaleY = inH / (float)outH,
            DstNormX = 1f / outW,
            DstNormY = 1f / outH,
            SrcNormX = 1f / inW,
            SrcNormY = 1f / inH,
            InWidth = (uint)inW,
            InHeight = (uint)inH,
            OutWidth = (uint)outW,
            OutHeight = (uint)outH,
        };
    }

    public void Process(Ingest ingest, RenderTexture output)
    {
        var src = ingest.Output!;
        double scale = (double)output.Width / src.Width;
        if (scale < 1 || scale > 2.0001)
        {
            Status = "NIS works from 1x to 2x; bicubic used for this size";
            _fallback.Process(ingest, output);
            return;
        }
        Status = "";
        if (_uavTex == null || _uavTex.Description.Width != output.Width || _uavTex.Description.Height != output.Height)
        {
            _uav?.Dispose();
            _uavTex?.Dispose();
            _uavTex = _g.Device.CreateTexture2D(new Texture2DDescription(Format.R8G8B8A8_UNorm, (uint)output.Width, (uint)output.Height, 1, 1, BindFlags.UnorderedAccess | BindFlags.ShaderResource));
            _uav = _g.Device.CreateUnorderedAccessView(_uavTex);
        }
        // NIS sharpens as part of scaling; the present pass adds RCAS on top if asked, so keep NIS mild.
        _g.Update(_cb, Constants(0.3f, src.Width, src.Height, output.Width, output.Height));
        var ctx = _g.Context;
        ctx.CSSetShader(_g.ComputeShader("nis/NIS_Main.hlsl", "main", Defines));
        ctx.CSSetConstantBuffer(0, _cb);
        ctx.CSSetSampler(0, _g.LinearClamp);
        ctx.CSSetShaderResource(0, src.Srv);
        ctx.CSSetShaderResource(1, _coefScaleSrv);
        ctx.CSSetShaderResource(2, _coefUsmSrv);
        ctx.CSSetUnorderedAccessView(0, _uav!, 0);
        ctx.Dispatch((uint)((output.Width + 31) / 32), (uint)((output.Height + 23) / 24), 1);
        ctx.CSUnsetUnorderedAccessView(0, 0);
        ctx.CSUnsetShaderResources(0, 3);
        ctx.CopyResource(output.Texture, _uavTex);
    }

    public void Dispose()
    {
        _uav?.Dispose();
        _uavTex?.Dispose();
        _coefScaleSrv.Dispose();
        _coefUsmSrv.Dispose();
        _coefScale.Dispose();
        _coefUsm.Dispose();
        _cb.Dispose();
        _fallback.Dispose();
    }
}
