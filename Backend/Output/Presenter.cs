using System.Runtime.InteropServices;
using Serilog;
using TretioDLSS.Engines;
using TretioDLSS.Graphics;
using TretioDLSS.Pipeline;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace TretioDLSS.Output;

public enum SyncMode
{
    Vrr,
    Tearing,
    Vsync,
}

public readonly record struct PresentedFrame(long Qpc, bool Generated, long RealIndex, double T);

public sealed record HdrConfig(double PaperWhite = 200, double Expand = 0.35, double Brightness = 0, double Contrast = 1, double Saturation = 1);

[StructLayout(LayoutKind.Sequential)]
internal struct HdrConstants
{
    public Vector4 A, B;
}

/// <summary>Where finished frames go: a window swap chain, or an offscreen target for headless runs.</summary>
public abstract class OutputSink : IDisposable
{
    protected readonly GraphicsDevice G;
    private readonly ID3D11Buffer _cb;
    private readonly ID3D11Buffer _copyCb;
    public double Sharpness { get; set; }
    public int Width { get; protected set; }
    public int Height { get; protected set; }
    public virtual bool DisplayPaces => false;
    public virtual double RefreshHz => 60;
    public List<PresentedFrame> PresentLog { get; } = new(4096);
    public bool RecordLog { get; set; }
    public Func<int, string?>? DumpPathFor { get; set; }
    public Overlay? Overlay { get; set; }
    public long Presented;

    /// <summary>HDR output in scRGB (FP16); null means SDR.</summary>
    public HdrConfig? Hdr { get; protected set; }
    public double LastDumpMaxNits { get; private set; } = double.NaN;
    private RenderTexture? _sdr;
    private ID3D11Buffer? _hdrCb;

    public void SetHdrLook(HdrConfig look)
    {
        if (Hdr != null) Hdr = look;
    }

    protected OutputSink(GraphicsDevice g)
    {
        G = g;
        _cb = g.CreateConstantBuffer(Marshal.SizeOf<FsrConstants>());
        _copyCb = g.CreateConstantBuffer(16);
    }

    /// <summary>Largest rectangle with the source aspect that fits the target, centered.</summary>
    public static (int X, int Y, int W, int H) Fit(int srcW, int srcH, int dstW, int dstH, string mode = "display")
    {
        if (srcW <= 0 || srcH <= 0 || dstW <= 0 || dstH <= 0) return (0, 0, Math.Max(1, dstW), Math.Max(1, dstH));
        double scale = Math.Min((double)dstW / srcW, (double)dstH / srcH);
        if (mode == "source") scale = Math.Min(1, scale);
        else if (mode == "integer") scale = Math.Max(1, Math.Floor(scale));
        int w = Math.Max(1, (int)Math.Round(srcW * scale));
        int h = Math.Max(1, (int)Math.Round(srcH * scale));
        w = Math.Min(w, dstW);
        h = Math.Min(h, dstH);
        return ((dstW - w) / 2, (dstH - h) / 2, w, h);
    }

    /// <summary>Draws the frame letterboxed into the given target; with HDR on, through an SDR pass and the HDR conversion.</summary>
    protected void Compose(ID3D11RenderTargetView target, RenderTexture frame)
    {
        if (Hdr is not HdrConfig hdr)
        {
            ComposeSdr(target, frame);
            return;
        }
        if (_sdr == null || _sdr.Width != Width || _sdr.Height != Height)
        {
            _sdr?.Dispose();
            _sdr = new RenderTexture(G.Device, Width, Height);
        }
        ComposeSdr(_sdr.Rtv, frame);
        _hdrCb ??= G.CreateConstantBuffer(Marshal.SizeOf<HdrConstants>());
        G.Update(_hdrCb, new HdrConstants
        {
            A = new Vector4((float)(hdr.PaperWhite / 80), (float)hdr.Expand, (float)hdr.Brightness, (float)hdr.Contrast),
            B = new Vector4((float)hdr.Saturation, 0, 0, 0),
        });
        G.Draw(target, G.PixelShader("util.hlsl", "PSHdr"), new Viewport(0, 0, Width, Height), [_sdr.Srv], _hdrCb);
    }

    private void ComposeSdr(ID3D11RenderTargetView target, RenderTexture frame)
    {
        var ctx = G.Context;
        ctx.ClearRenderTargetView(target, new Color4(0.043f, 0.051f, 0.063f, 1f));
        bool down = frame.Width > Width || frame.Height > Height;
        int fw = down ? frame.Width / 2 : frame.Width, fh = down ? frame.Height / 2 : frame.Height;
        int x = (Width - fw) / 2, y = (Height - fh) / 2;
        var vp = new Viewport(x, y, fw, fh);
        float? stops = Rcas.Stops(Sharpness);
        if (down)
        {
            G.Update(_copyCb, new Int4(x, y, 0, 0));
            G.Draw(target, G.PixelShader("util.hlsl", "PSDown2"), vp, [frame.Srv], _copyCb);
        }
        else if (stops is float s)
        {
            G.Update(_cb, Rcas.Constants(s, x, y));
            G.Draw(target, G.PixelShader("fsr.hlsl", "PSRcas"), vp, [frame.Srv], _cb);
        }
        else
        {
            G.Update(_copyCb, new Int4(x, y, 0, 0));
            G.Draw(target, G.PixelShader("util.hlsl", "PSCopy"), vp, [frame.Srv], _copyCb);
        }
    }

    public abstract void Present(RenderTexture frame, PresentedFrame info, int index);

    /// <summary>Blocks until the sink can take another frame without queueing (waitable swap chain).</summary>
    public virtual void WaitReady(int timeoutMs) { }

    protected void Record(PresentedFrame f)
    {
        Interlocked.Increment(ref Presented);
        if (RecordLog) lock (PresentLog) PresentLog.Add(f);
    }

    private readonly List<Task> _dumps = new();

    /// <summary>Reads the texture back and writes the PNG on a worker, so the pipeline thread only pays for the copy.</summary>
    protected unsafe void DumpTexture(ID3D11Texture2D texture, string path)
    {
        var desc = texture.Description;
        desc.BindFlags = BindFlags.None;
        desc.Usage = ResourceUsage.Staging;
        desc.CPUAccessFlags = CpuAccessFlags.Read;
        desc.MiscFlags = ResourceOptionFlags.None;
        using var staging = G.Device.CreateTexture2D(desc);
        G.Context.CopyResource(staging, texture);
        int w = (int)desc.Width, h = (int)desc.Height;
        bool rgba = desc.Format is Format.R8G8B8A8_UNorm or Format.R8G8B8A8_UNorm_SRgb or Format.R16G16B16A16_Float;
        bool half = desc.Format == Format.R16G16B16A16_Float;
        int bpp = half ? 8 : 4;
        var raw = new byte[w * h * bpp];
        var map = G.Context.Map(staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        try
        {
            fixed (byte* dst = raw)
                for (int row = 0; row < h; row++)
                    Buffer.MemoryCopy((byte*)map.DataPointer + row * map.RowPitch, dst + row * w * bpp, w * bpp, w * bpp);
        }
        finally
        {
            G.Context.Unmap(staging, 0);
        }
        double paper = Hdr?.PaperWhite ?? 200;
        lock (_dumps) _dumps.Add(Task.Run(() =>
        {
            var pixels = half ? FromScRgb(raw, w, h, paper) : raw;
            WritePng(pixels, w, h, rgba, path);
        }));
    }

    /// <summary>scRGB to an 8-bit picture with paper white as white; records the brightest value in nits.</summary>
    private unsafe byte[] FromScRgb(byte[] raw, int w, int h, double paper)
    {
        var pixels = new byte[w * h * 4];
        double max = 0;
        fixed (byte* p = raw)
        {
            var src = (Half*)p;
            for (int i = 0; i < w * h; i++)
            {
                for (int c = 0; c < 3; c++)
                {
                    double v = (double)src[i * 4 + c];
                    max = Math.Max(max, v);
                    double sdr = Math.Clamp(v * 80 / paper, 0, 1);
                    pixels[i * 4 + c] = (byte)Math.Round(255 * (sdr <= 0.0031308 ? sdr * 12.92 : 1.055 * Math.Pow(sdr, 1 / 2.4) - 0.055));
                }
                pixels[i * 4 + 3] = 255;
            }
        }
        LastDumpMaxNits = max * 80;
        return pixels;
    }

    private static unsafe void WritePng(byte[] pixels, int w, int h, bool rgba, string path)
    {
        using var bmp = new System.Drawing.Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        var bd = bmp.LockBits(new System.Drawing.Rectangle(0, 0, w, h), System.Drawing.Imaging.ImageLockMode.WriteOnly, bmp.PixelFormat);
        fixed (byte* src = pixels)
        {
            for (int row = 0; row < h; row++)
            {
                byte* s = src + row * w * 4;
                byte* d = (byte*)bd.Scan0 + row * bd.Stride;
                for (int col = 0; col < w; col++, s += 4, d += 4)
                {
                    if (rgba) { d[0] = s[2]; d[1] = s[1]; d[2] = s[0]; }
                    else { d[0] = s[0]; d[1] = s[1]; d[2] = s[2]; }
                    d[3] = 255;
                }
            }
        }
        bmp.UnlockBits(bd);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
    }

    /// <summary>Waits for pending frame dumps to be written.</summary>
    public void FlushDumps()
    {
        Task[] all;
        lock (_dumps) all = _dumps.ToArray();
        Task.WaitAll(all, TimeSpan.FromSeconds(30));
    }

    public virtual void Dispose()
    {
        FlushDumps();
        _sdr?.Dispose();
        _hdrCb?.Dispose();
        _cb.Dispose();
        _copyCb.Dispose();
    }
}

/// <summary>Headless target with the size of the output display, presents complete instantly.</summary>
public sealed class OffscreenSink : OutputSink
{
    private readonly RenderTexture _target;

    public OffscreenSink(GraphicsDevice g, int width, int height, HdrConfig? hdr = null) : base(g)
    {
        Width = width;
        Height = height;
        Hdr = hdr;
        _target = new RenderTexture(g.Device, width, height, hdr != null ? Format.R16G16B16A16_Float : Format.R8G8B8A8_UNorm);
    }

    public override void Present(RenderTexture frame, PresentedFrame info, int index)
    {
        Compose(_target.Rtv, frame);
        if (DumpPathFor?.Invoke(index) is string path) DumpTexture(_target.Texture, path);
        G.Context.Flush();
        Record(info);
    }

    public override void Dispose()
    {
        _target.Dispose();
        base.Dispose();
    }
}

/// <summary>Flip-model swap chain on the output window.</summary>
public sealed class SwapChainSink : OutputSink
{
    private readonly OutputWindow _window;
    private readonly IDXGISwapChain2 _swap;
    private readonly IntPtr _waitable;
    private readonly bool _tearingSupported;
    private ID3D11Texture2D? _back;
    private ID3D11RenderTargetView? _backRtv;
    private readonly double _refresh;
    private const SwapChainFlags Flags = SwapChainFlags.AllowTearing | SwapChainFlags.FrameLatencyWaitableObject;

    public SyncMode Sync { get; set; }
    public override bool DisplayPaces => Sync == SyncMode.Vsync;
    public override double RefreshHz => _refresh;
    public bool TearingSupported => _tearingSupported;
    public OutputWindow Window => _window;

    private readonly Format _format;

    /// <param name="hdr">Used only when the display is in HDR mode; then the swap chain is FP16 scRGB.</param>
    public SwapChainSink(GraphicsDevice g, OutputWindow window, SyncMode sync, double refreshHz, HdrConfig? hdr = null, bool displayHdr = false) : base(g)
    {
        Hdr = displayHdr ? hdr : null;
        _format = Hdr != null ? Format.R16G16B16A16_Float : Format.R8G8B8A8_UNorm;
        _window = window;
        Sync = sync;
        _refresh = refreshHz;
        using (var f5 = g.Factory.QueryInterfaceOrNull<IDXGIFactory5>())
            _tearingSupported = f5?.PresentAllowTearing ?? false;
        Width = Math.Max(1, window.Width);
        Height = Math.Max(1, window.Height);
        var desc = new SwapChainDescription1
        {
            Width = (uint)Width,
            Height = (uint)Height,
            Format = _format,
            SampleDescription = new SampleDescription(1, 0),
            BufferUsage = Usage.RenderTargetOutput,
            BufferCount = 2,
            Scaling = Scaling.Stretch,
            SwapEffect = SwapEffect.FlipDiscard,
            AlphaMode = AlphaMode.Ignore,
            Flags = Flags,
        };
        using var sc1 = g.Factory.CreateSwapChainForHwnd(g.Device, window.Handle, desc, null, null);
        _swap = sc1.QueryInterface<IDXGISwapChain2>();
        _swap.MaximumFrameLatency = 1;
        _waitable = _swap.FrameLatencyWaitableObject;
        if (Hdr != null)
        {
            using var sc3 = _swap.QueryInterface<IDXGISwapChain3>();
            var space = ColorSpaceType.RgbFullG10NoneP709;
            if (sc3.CheckColorSpaceSupport(space).HasFlag(SwapChainColorSpaceSupportFlags.Present))
            {
                sc3.SetColorSpace1(space);
                Serilog.Log.Information("HDR output: scRGB FP16, paper white {Paper} nits", Hdr.PaperWhite);
            }
            else
            {
                Serilog.Log.Warning("The display does not take scRGB; staying in SDR");
                Hdr = null;
            }
        }
        g.Factory.MakeWindowAssociation(window.Handle, WindowAssociationFlags.IgnoreAltEnter);
        GetBack();
        Serilog.Log.Information("Swap chain {W}x{H} on output window, sync {Sync}, tearing {Tear}", Width, Height, sync, _tearingSupported);
    }

    private void GetBack()
    {
        _back = _swap.GetBuffer<ID3D11Texture2D>(0);
        _backRtv = G.Device.CreateRenderTargetView(_back);
        Overlay?.Attach(_back);
    }

    private void ResizeIfNeeded()
    {
        int w = Math.Max(1, _window.Width), h = Math.Max(1, _window.Height);
        if (w == Width && h == Height) return;
        Overlay?.Detach();
        _backRtv?.Dispose();
        _back?.Dispose();
        _swap.ResizeBuffers(2, (uint)w, (uint)h, _format, Flags).CheckError();
        Width = w;
        Height = h;
        GetBack();
    }

    [DllImport("kernel32.dll")]
    private static extern uint WaitForSingleObjectEx(IntPtr handle, uint ms, bool alertable);

    public override void WaitReady(int timeoutMs) => WaitForSingleObjectEx(_waitable, (uint)timeoutMs, true);

    public override void Present(RenderTexture frame, PresentedFrame info, int index)
    {
        ResizeIfNeeded();
        if (Overlay != null && _back != null && !Overlay.Attached) Overlay.Attach(_back, _format, Hdr == null ? 1f : (float)(Hdr.PaperWhite / 80));
        Compose(_backRtv!, frame);
        Overlay?.Draw();
        if (DumpPathFor?.Invoke(index) is string path) DumpTexture(_back!, path);
        SharpGen.Runtime.Result hr = Sync switch
        {
            SyncMode.Vsync => _swap.Present(1, PresentFlags.None),
            _ => _swap.Present(0, _tearingSupported ? PresentFlags.AllowTearing : PresentFlags.None),
        };
        if (hr.Failure)
        {
            if (hr.Code == Vortice.DXGI.ResultCode.DeviceRemoved.Code || hr.Code == Vortice.DXGI.ResultCode.DeviceReset.Code)
                throw new DeviceLostException(hr.ToString());
            Serilog.Log.Debug("Present returned {Hr}", hr);
        }
        Record(info with { Qpc = Qpc.Now });
        TrackRefreshes();
    }

    private uint _lastPresentCount, _lastRefreshCount;
    private readonly long[] _refreshHistogram = new long[9];

    /// <summary>
    /// How many refreshes each frame stayed on screen, from DXGI frame statistics (fixed-refresh
    /// pacing). Index 8 collects 8 and more. Empty in composed (windowed) mode.
    /// </summary>
    public long[] RefreshHistogram
    {
        get { lock (_refreshHistogram) return (long[])_refreshHistogram.Clone(); }
    }

    private void TrackRefreshes()
    {
        if (Sync != SyncMode.Vsync) return;
        if (_swap.GetFrameStatistics(out var st).Failure) return;
        if (_lastPresentCount != 0 && st.PresentCount > _lastPresentCount)
        {
            uint presents = st.PresentCount - _lastPresentCount;
            uint refreshes = st.PresentRefreshCount - _lastRefreshCount;
            if (presents == 1)
                lock (_refreshHistogram) _refreshHistogram[Math.Min(8, (int)refreshes)]++;
        }
        _lastPresentCount = st.PresentCount;
        _lastRefreshCount = st.PresentRefreshCount;
    }

    public override void Dispose()
    {
        Overlay?.Detach();
        _backRtv?.Dispose();
        _back?.Dispose();
        _swap.Dispose();
        base.Dispose();
    }
}

public sealed class DeviceLostException(string message) : Exception(message);
