using System.Runtime.InteropServices;
using TretioDLSS.Graphics;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace TretioDLSS.Pipeline;

[StructLayout(LayoutKind.Sequential)]
internal struct IngestConstants
{
    public Vector4 RowR, RowG, RowB, Range, Size;
}

public sealed record ColorSpec(string Matrix, string Range)
{
    public bool Bt709 => Matrix != "bt601";
    public bool Limited => Range != "full";
}

/// <summary>
/// Uploads source frames to the GPU and converts them to the RGBA working format.
/// NV12 is kept as a native texture as well, for engines that take YUV directly.
/// </summary>
public sealed class Ingest : IDisposable
{
    private readonly GraphicsDevice _g;
    private readonly ID3D11Buffer _cb;
    private readonly ID3D11Texture2D?[] _nv12Staging = new ID3D11Texture2D?[3];
    private int _stagingIndex;
    private ID3D11Texture2D? _nv12;
    private ID3D11ShaderResourceView? _lumaSrv, _chromaSrv;
    private ID3D11Texture2D? _packed;
    private ID3D11ShaderResourceView? _packedSrv;
    private RenderTexture? _output;
    private int _w, _h;
    private PixelLayout _layout;
    private ColorSpec _color = new("bt709", "limited");
    private readonly ID3D11Buffer _patternCb;

    public RenderTexture? Output => _output;

    /// <summary>The raw NV12 texture of the latest frame (decoder-bindable), when the source is NV12.</summary>
    public ID3D11Texture2D? Nv12Texture => _nv12;

    public Ingest(GraphicsDevice g)
    {
        _g = g;
        _cb = g.CreateConstantBuffer(Marshal.SizeOf<IngestConstants>());
        _patternCb = g.CreateConstantBuffer(16);
    }

    public void SetColor(ColorSpec color) => _color = color;

    private void Ensure(PixelLayout layout, int w, int h)
    {
        if (layout == _layout && w == _w && h == _h && _output != null) return;
        Release();
        _layout = layout;
        _w = w;
        _h = h;
        var dev = _g.Device;
        switch (layout)
        {
            case PixelLayout.Nv12:
                for (int i = 0; i < _nv12Staging.Length; i++)
                    _nv12Staging[i] = dev.CreateTexture2D(new Texture2DDescription(Format.NV12, (uint)w, (uint)h, 1, 1, BindFlags.None, ResourceUsage.Staging, CpuAccessFlags.Write));
                var desc = new Texture2DDescription(Format.NV12, (uint)w, (uint)h, 1, 1, BindFlags.ShaderResource | BindFlags.Decoder);
                try { _nv12 = dev.CreateTexture2D(desc); }
                catch
                {
                    desc.BindFlags = BindFlags.ShaderResource;
                    _nv12 = dev.CreateTexture2D(desc);
                }
                _lumaSrv = dev.CreateShaderResourceView(_nv12, new ShaderResourceViewDescription(_nv12, ShaderResourceViewDimension.Texture2D, Format.R8_UNorm));
                _chromaSrv = dev.CreateShaderResourceView(_nv12, new ShaderResourceViewDescription(_nv12, ShaderResourceViewDimension.Texture2D, Format.R8G8_UNorm));
                break;
            case PixelLayout.Yuy2:
                _packed = dev.CreateTexture2D(new Texture2DDescription(Format.R8G8B8A8_UNorm, (uint)(w / 2), (uint)h, 1, 1, BindFlags.ShaderResource, ResourceUsage.Dynamic, CpuAccessFlags.Write));
                _packedSrv = dev.CreateShaderResourceView(_packed);
                break;
            case PixelLayout.Bgra8:
                _packed = dev.CreateTexture2D(new Texture2DDescription(Format.B8G8R8A8_UNorm, (uint)w, (uint)h, 1, 1, BindFlags.ShaderResource, ResourceUsage.Dynamic, CpuAccessFlags.Write));
                _packedSrv = dev.CreateShaderResourceView(_packed);
                break;
        }
        _output = new RenderTexture(dev, w, h);
    }

    /// <summary>Uploads a CPU frame. Set convert to false when the next stage takes the raw texture.</summary>
    public unsafe void Upload(CpuFrame f, bool convert = true)
    {
        Ensure(f.Layout, f.Width, f.Height);
        var ctx = _g.Context;
        switch (f.Layout)
        {
            case PixelLayout.Nv12:
            {
                // A ring of staging textures, so mapping never waits for the previous upload.
                var staging = _nv12Staging[_stagingIndex]!;
                _stagingIndex = (_stagingIndex + 1) % _nv12Staging.Length;
                var map = ctx.Map(staging, 0, MapMode.Write, Vortice.Direct3D11.MapFlags.None);
                byte* dst = (byte*)map.DataPointer;
                fixed (byte* src = f.Data)
                {
                    int rows = f.Height + f.Height / 2;
                    for (int y = 0; y < rows; y++)
                        Buffer.MemoryCopy(src + y * f.Stride, dst + y * map.RowPitch, map.RowPitch, f.Width);
                }
                ctx.Unmap(staging, 0);
                ctx.CopyResource(_nv12!, staging);
                if (convert) Convert(true);
                break;
            }
            default:
            {
                var map = ctx.Map(_packed!, 0, MapMode.WriteDiscard, Vortice.Direct3D11.MapFlags.None);
                byte* dst = (byte*)map.DataPointer;
                int rowBytes = f.Layout == PixelLayout.Yuy2 ? f.Width * 2 : f.Width * 4;
                fixed (byte* src = f.Data)
                {
                    for (int y = 0; y < f.Height; y++)
                        Buffer.MemoryCopy(src + y * f.Stride, dst + y * map.RowPitch, map.RowPitch, rowBytes);
                }
                ctx.Unmap(_packed!, 0);
                Convert(false);
                break;
            }
        }
    }

    /// <summary>Copies the cropped region of a GPU frame into the working texture.</summary>
    /// <summary>Window sources: crop black bars around the picture (letterboxed video, client borders).</summary>
    public bool TrimBlackBars { get; set; }
    public (int X, int Y, int W, int H) Trim { get; private set; }

    private ID3D11Texture2D? _trimStaging;
    private int _trimFrames, _trimPendingAt = -1;
    private (int X, int Y, int W, int H) _trimCandidate;
    private const int TrimEvery = 30;

    public void UploadGpu(GpuFrame f)
    {
        if (_gpuW != f.Width || _gpuH != f.Height)
        {
            _gpuW = f.Width;
            _gpuH = f.Height;
            Trim = (0, 0, f.Width, f.Height);
            _trimCandidate = Trim;
            _trimStaging?.Dispose();
            _trimStaging = null;
            _trimPendingAt = -1;
        }
        if (TrimBlackBars) UpdateTrim(f);
        var t = Trim;
        if (_output == null || _layout != PixelLayout.Bgra8 || _output.Width != t.W || _output.Height != t.H)
        {
            Release();
            _layout = PixelLayout.Bgra8;
            _w = t.W;
            _h = t.H;
            _output = new RenderTexture(_g.Device, t.W, t.H, Format.B8G8R8A8_UNorm);
        }
        int x = f.CropX + t.X, y = f.CropY + t.Y;
        _g.Context.CopySubresourceRegion(_output.Texture, 0, 0, 0, 0, f.Texture, 0, new Box(x, y, 0, x + t.W, y + t.H, 1));
    }

    private int _gpuW, _gpuH;

    /// <summary>Copies a frame for analysis every TrimEvery frames and reads it two frames later, so nothing waits.</summary>
    private unsafe void UpdateTrim(GpuFrame f)
    {
        _trimFrames++;
        var ctx = _g.Context;
        if (_trimPendingAt >= 0 && _trimFrames - _trimPendingAt >= 2)
        {
            var map = ctx.Map(_trimStaging!, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
            (int X, int Y, int W, int H) found;
            try
            {
                var span = new ReadOnlySpan<byte>((void*)map.DataPointer, (int)map.RowPitch * f.Height);
                found = BlackBars.Detect(span, (int)map.RowPitch, f.Width, f.Height);
                if (Environment.GetEnvironmentVariable("TRETIO_TRIMDUMP") is string dumpPath && !File.Exists(dumpPath))
                {
                    using var bmp = new System.Drawing.Bitmap(f.Width, f.Height, (int)map.RowPitch, System.Drawing.Imaging.PixelFormat.Format32bppRgb, map.DataPointer);
                    bmp.Save(dumpPath, System.Drawing.Imaging.ImageFormat.Png);
                }
            }
            finally
            {
                ctx.Unmap(_trimStaging!, 0);
            }
            _trimPendingAt = -1;
            // Two detections in a row must agree before the crop changes.
            if (found == _trimCandidate && found != Trim)
            {
                Trim = found;
                Serilog.Log.Information("Black bars trimmed: picture at {X},{Y} size {W}x{H} of {FW}x{FH}", found.X, found.Y, found.W, found.H, f.Width, f.Height);
            }
            _trimCandidate = found;
        }
        if (_trimPendingAt < 0 && _trimFrames % TrimEvery == 0)
        {
            _trimStaging ??= _g.Device.CreateTexture2D(new Texture2DDescription(Format.B8G8R8A8_UNorm, (uint)f.Width, (uint)f.Height, 1, 1, BindFlags.None, ResourceUsage.Staging, CpuAccessFlags.Read));
            ctx.CopySubresourceRegion(_trimStaging, 0, 0, 0, 0, f.Texture, 0, new Box(f.CropX, f.CropY, 0, f.CropX + f.Width, f.CropY + f.Height, 1));
            _trimPendingAt = _trimFrames;
        }
    }

    /// <summary>Draws the test pattern on the GPU (the BGRA source path).</summary>
    public void DrawPattern(DrawFrame f, float barStep)
    {
        Ensure(PixelLayout.Bgra8, f.Width, f.Height);
        _g.Update(_patternCb, new Vector4(f.ContentIndex, f.Width, f.Height, barStep));
        var ps = _g.PixelShader("util.hlsl", "PSPattern");
        _g.Draw(_output!.Rtv, ps, _output.Viewport, ReadOnlySpan<ID3D11ShaderResourceView?>.Empty, null, null, _patternCb);
    }


    private void Convert(bool nv12)
    {
        var c = BuildConstants(_color, _w, _h);
        _g.Update(_cb, c);
        if (nv12)
            _g.Draw(_output!.Rtv, _g.PixelShader("ingest.hlsl", "PSNv12"), _output.Viewport, [_lumaSrv, _chromaSrv], _cb);
        else if (_layout == PixelLayout.Yuy2)
            _g.Draw(_output!.Rtv, _g.PixelShader("ingest.hlsl", "PSYuy2"), _output.Viewport, [null, null, _packedSrv], _cb);
        else
            _g.Draw(_output!.Rtv, _g.PixelShader("ingest.hlsl", "PSBgra"), _output.Viewport, [null, null, _packedSrv], _cb);
    }

    internal static IngestConstants BuildConstants(ColorSpec color, int w, int h)
    {
        var c = new IngestConstants();
        if (color.Bt709)
        {
            c.RowR = new Vector4(1, 0, 1.5748f, 0);
            c.RowG = new Vector4(1, -0.187324f, -0.468124f, 0);
            c.RowB = new Vector4(1, 1.8556f, 0, 0);
        }
        else
        {
            c.RowR = new Vector4(1, 0, 1.402f, 0);
            c.RowG = new Vector4(1, -0.344136f, -0.714136f, 0);
            c.RowB = new Vector4(1, 1.772f, 0, 0);
        }
        c.Range = color.Limited
            ? new Vector4(16f / 255f, 255f / 219f, 128f / 255f, 255f / 224f)
            : new Vector4(0, 1, 128f / 255f, 1);
        c.Size = new Vector4(w, h, 1f / w, 1f / h);
        return c;
    }

    private void Release()
    {
        _lumaSrv?.Dispose(); _lumaSrv = null;
        _chromaSrv?.Dispose(); _chromaSrv = null;
        _nv12?.Dispose(); _nv12 = null;
        for (int i = 0; i < _nv12Staging.Length; i++)
        {
            _nv12Staging[i]?.Dispose();
            _nv12Staging[i] = null;
        }
        _packedSrv?.Dispose(); _packedSrv = null;
        _packed?.Dispose(); _packed = null;
        _output?.Dispose(); _output = null;
    }

    public void Dispose()
    {
        Release();
        _trimStaging?.Dispose();
        _cb.Dispose();
        _patternCb.Dispose();
    }
}
