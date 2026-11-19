using System.Runtime.InteropServices;
using Serilog;
using TretioDLSS.Graphics;
using TretioDLSS.Pipeline;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace TretioDLSS.Engines;

public interface IUpscaler : IDisposable
{
    string Id { get; }

    /// <summary>Scales the ingest result into the output texture (which has the target size).</summary>
    void Process(Ingest ingest, RenderTexture output);

    /// <summary>Status line for the UI, for example "active, level 4".</summary>
    string Status { get; }
}

[StructLayout(LayoutKind.Sequential)]
internal struct ScaleConstants
{
    public Vector4 SrcSize;
    public Vector4 SrcRect;
}

public sealed class ShaderUpscaler : IUpscaler
{
    private readonly GraphicsDevice _g;
    private readonly string _entry;
    private readonly ID3D11Buffer _cb;

    public string Id { get; }
    public string Status => "";

    public ShaderUpscaler(GraphicsDevice g, string id)
    {
        _g = g;
        Id = id;
        _entry = id switch { "bicubic" => "PSBicubic", "point" => "PSPoint", _ => "PSBilinear" };
        _cb = g.CreateConstantBuffer(Marshal.SizeOf<ScaleConstants>());
    }

    public void Process(Ingest ingest, RenderTexture output)
    {
        var src = ingest.Output!;
        _g.Update(_cb, new ScaleConstants
        {
            SrcSize = new Vector4(src.Width, src.Height, 1f / src.Width, 1f / src.Height),
            SrcRect = new Vector4(0, 0, 1, 1),
        });
        _g.Draw(output.Rtv, _g.PixelShader("scale.hlsl", _entry), output.Viewport, [src.Srv], _cb);
    }

    public void Dispose() => _cb.Dispose();
}

[StructLayout(LayoutKind.Sequential)]
internal struct FsrConstants
{
    public uint C0x, C0y, C0z, C0w, C1x, C1y, C1z, C1w, C2x, C2y, C2z, C2w, C3x, C3y, C3z, C3w;
    public int OriginX, OriginY, Pad0, Pad1;
}

/// <summary>FSR 1 EASU. The constants follow FsrEasuCon in ffx_fsr1.h.</summary>
public sealed class Fsr1Upscaler : IUpscaler
{
    private readonly GraphicsDevice _g;
    private readonly ID3D11Buffer _cb;

    public string Id => "fsr1";
    public string Status => "";

    public Fsr1Upscaler(GraphicsDevice g)
    {
        _g = g;
        _cb = g.CreateConstantBuffer(Marshal.SizeOf<FsrConstants>());
    }

    internal static FsrConstants EasuConstants(float inW, float inH, float outW, float outH)
    {
        static uint U(float f) => BitConverter.SingleToUInt32Bits(f);
        return new FsrConstants
        {
            C0x = U(inW / outW),
            C0y = U(inH / outH),
            C0z = U(0.5f * inW / outW - 0.5f),
            C0w = U(0.5f * inH / outH - 0.5f),
            C1x = U(1f / inW),
            C1y = U(1f / inH),
            C1z = U(1f / inW),
            C1w = U(-1f / inH),
            C2x = U(-1f / inW),
            C2y = U(2f / inH),
            C2z = U(1f / inW),
            C2w = U(2f / inH),
            C3x = U(0),
            C3y = U(4f / inH),
        };
    }

    public void Process(Ingest ingest, RenderTexture output)
    {
        var src = ingest.Output!;
        _g.Update(_cb, EasuConstants(src.Width, src.Height, output.Width, output.Height));
        _g.Draw(output.Rtv, _g.PixelShader("fsr.hlsl", "PSEasu"), output.Viewport, [src.Srv], _cb);
    }

    public void Dispose() => _cb.Dispose();
}

/// <summary>FSR 1 RCAS sharpening, used by the present pass after any upscaler.</summary>
public static class Rcas
{
    /// <summary>UI sharpness 0..1 to RCAS stops (0 = strongest). Returns null for "off".</summary>
    public static float? Stops(double sharpness) => sharpness <= 0.001 ? null : (float)((1 - sharpness) * 2.0);

    internal static FsrConstants Constants(float stops, int originX, int originY)
    {
        float s = MathF.Pow(2, -stops);
        ushort h = BitConverter.HalfToUInt16Bits((Half)s);
        return new FsrConstants
        {
            C0x = BitConverter.SingleToUInt32Bits(s),
            C0y = (uint)(h | (h << 16)),
            OriginX = originX,
            OriginY = originY,
        };
    }
}
