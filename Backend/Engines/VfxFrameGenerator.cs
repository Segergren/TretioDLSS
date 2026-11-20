using Serilog;
using TretioDLSS.Graphics;
using Vortice.Direct3D11;

namespace TretioDLSS.Engines;

/// <summary>NVIDIA Video Frame Generation: two RGBA frames in, the frame at t out.</summary>
public sealed unsafe class VfxFrameGenerator : IFrameGenerator
{
    private readonly GraphicsDevice _g;
    private readonly uint _mode;
    private readonly VfxEffect _fx;
    private int _w, _h;
    private ID3D11Texture2D? _texIn0, _texIn1, _texOut;
    private NvCVImage* _d3dIn0, _d3dIn1, _d3dOut, _buf0, _buf1, _bufOut, _tmp;
    private readonly List<double> _callMs = new();
    private (IntPtr Tex, long Stamp) _key0, _key1;

    public string Id => "vfx-vfg";
    public bool LastWasCopy => false;

    public static IFrameGenerator? TryCreate(GraphicsDevice g, string model)
    {
        var dir = VfxRuntime.FindFor("vfx-vfg");
        if (dir == null)
        {
            Log.Warning("NVIDIA Frame Generation is not downloaded; frame generation stays off");
            return null;
        }
        if (!g.IsNvidia)
        {
            Log.Warning("NVIDIA Frame Generation needs the pipeline on the NVIDIA GPU; it runs on {Adapter}", g.AdapterName);
            return null;
        }
        try
        {
            return new VfxFrameGenerator(g, dir, model);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "NVIDIA Frame Generation failed to start");
            return null;
        }
    }

    private VfxFrameGenerator(GraphicsDevice g, string dir, string model)
    {
        _g = g;
        _mode = model switch { "low" => 0u, "high" => 2u, _ => 1u };
        _fx = new VfxEffect(dir, "VideoFrameGeneration", "NVIDIA Frame Generation");
        Log.Information("NVIDIA Frame Generation from {Dir}, model {Model}", dir, model);
    }

    private void Setup(int w, int h)
    {
        ReleaseImages();
        _w = w;
        _h = h;
        _key0 = _key1 = default;
        var desc = new Texture2DDescription(Vortice.DXGI.Format.R8G8B8A8_UNorm, (uint)w, (uint)h, 1, 1, BindFlags.ShaderResource | BindFlags.RenderTarget);
        _texIn0 = _g.Device.CreateTexture2D(desc);
        _texIn1 = _g.Device.CreateTexture2D(desc);
        _texOut = _g.Device.CreateTexture2D(desc);
        _d3dIn0 = _fx.FromTexture(_texIn0);
        _d3dIn1 = _fx.FromTexture(_texIn1);
        _d3dOut = _fx.FromTexture(_texOut);
        _buf0 = _fx.GpuImage(w, h);
        _buf1 = _fx.GpuImage(w, h);
        _bufOut = _fx.GpuImage(w, h);
        _tmp = VfxEffect.NewImage();

        var started = System.Diagnostics.Stopwatch.StartNew();
        _fx.SetStream();
        _fx.SetU32("InputWidth", (uint)w);
        _fx.SetU32("InputHeight", (uint)h);
        _fx.SetU32("Mode", _mode);
        _fx.SetU32("AutomaticShotChangeDetectionEnabled", 1);
        _fx.Load();
        _fx.SetImage("DstImage0", _bufOut);
        _fx.SetU32("FrameMultiplier", 0);
        // One run on empty frames, so the first real frame does not pay the CUDA warm-up.
        _fx.SetImage("SrcImage0", _buf0);
        _fx.SetImage("SrcImage1", _buf1);
        _fx.SetF32("Timestep", 0.5f);
        _fx.Run();
        Log.Information("NVIDIA Frame Generation loaded for {W} x {H} in {Ms} ms", w, h, started.ElapsedMilliseconds);
    }

    public void Prepare(int width, int height)
    {
        if (_texIn0 == null || width != _w || height != _h) Setup(width, height);
    }

    public void Generate(RenderTexture previous, RenderTexture current, float t, RenderTexture output)
    {
        if (_texIn0 == null || current.Width != _w || current.Height != _h) Setup(current.Width, current.Height);
        long start = System.Diagnostics.Stopwatch.GetTimestamp();
        try { GenerateCore(previous, current, t, output); }
        finally { if (_callMs.Count < 100_000) _callMs.Add(System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds); }
    }

    private void GenerateCore(RenderTexture previous, RenderTexture current, float t, RenderTexture output)
    {
        if (output.Format != Vortice.DXGI.Format.R8G8B8A8_UNorm || output.Width != _w || output.Height != _h)
            throw new InvalidOperationException($"Frame generation output must be {_w} x {_h} RGBA8");

        // Inputs stay in the CUDA buffers: x2 uploads one new frame per pair, x3 and x4 none after the first.
        var prevKey = Key(previous);
        var curKey = Key(current);
        if (prevKey.Stamp != 0 && _key1 == prevKey && _key0 != prevKey)
        {
            var b = _buf0;
            _buf0 = _buf1;
            _buf1 = b;
            (_key0, _key1) = (_key1, _key0);
        }
        bool load0 = prevKey.Stamp == 0 || _key0 != prevKey;
        bool load1 = curKey.Stamp == 0 || _key1 != curKey;
        if (load0) CopyIn(previous, _texIn0!);
        if (load1) CopyIn(current, _texIn1!);
        if (load0) _fx.Map(_d3dIn0);
        if (load1) _fx.Map(_d3dIn1);
        _fx.Map(_d3dOut);
        try
        {
            _key0 = _key1 = default;
            if (load0) _fx.Transfer(_d3dIn0, _buf0, _tmp);
            if (load1) _fx.Transfer(_d3dIn1, _buf1, _tmp);
            // The SDK wants both inputs set again for every run, even with unchanged buffers.
            _fx.SetImage("SrcImage0", _buf0);
            _fx.SetImage("SrcImage1", _buf1);
            _fx.SetU32("ShotChange", 0);
            _fx.SetF32("Timestep", Math.Clamp(t, 0.001f, 0.999f));
            _fx.Run();
            _fx.Transfer(_bufOut, _d3dOut, _tmp);
            _key0 = prevKey;
            _key1 = curKey;
        }
        finally
        {
            _fx.Unmap(_d3dOut);
            if (load1) _fx.Unmap(_d3dIn1);
            if (load0) _fx.Unmap(_d3dIn0);
        }
        _g.Context.CopyResource(output.Texture, _texOut!);
    }

    private static (IntPtr Tex, long Stamp) Key(RenderTexture r) => (r.Texture.NativePointer, r.Stamp);

    private void CopyIn(RenderTexture src, ID3D11Texture2D dst)
    {
        if (src.Format != Vortice.DXGI.Format.R8G8B8A8_UNorm) throw new InvalidOperationException($"Frame generation input is {src.Format}, expected RGBA8");
        _g.Context.CopyResource(dst, src.Texture);
    }

    public void Reset() => _key0 = _key1 = default;

    private void ReleaseImages()
    {
        _fx.Free(ref _d3dIn0);
        _fx.Free(ref _d3dIn1);
        _fx.Free(ref _d3dOut);
        _fx.Free(ref _buf0);
        _fx.Free(ref _buf1);
        _fx.Free(ref _bufOut);
        _fx.Free(ref _tmp);
        _texIn0?.Dispose();
        _texIn1?.Dispose();
        _texOut?.Dispose();
        _texIn0 = _texIn1 = _texOut = null;
    }

    public void Dispose()
    {
        if (_callMs.Count > 0)
        {
            var sorted = _callMs.Order().ToList();
            Log.Information("NVIDIA Frame Generation: {Count} frames, CPU time per call p50 {P50:F2} ms, p99 {P99:F2} ms, max {Max:F2} ms",
                sorted.Count, sorted[sorted.Count / 2], sorted[(int)(sorted.Count * 0.99)], sorted[^1]);
        }
        ReleaseImages();
        _fx.Dispose();
    }
}
