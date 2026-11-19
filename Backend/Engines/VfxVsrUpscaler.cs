using Serilog;
using TretioDLSS.Graphics;
using TretioDLSS.Pipeline;

namespace TretioDLSS.Engines;

/// <summary>
/// NVIDIA Video Super Resolution from the Video Effects SDK. Clean sources use the
/// HighBitrate models, compressed ones the standard models, which also remove compression artifacts.
/// </summary>
public sealed unsafe class VfxVsrUpscaler : IUpscaler
{
    private const int MinW = 640, MinH = 360, MaxW = 3840, MaxH = 2160;

    private readonly GraphicsDevice _g;
    private readonly VfxEffect _fx;
    private readonly uint _quality, _deblur;
    private readonly string _label;
    private readonly ShaderUpscaler _copy, _fallback;
    private RenderTexture? _in, _out;
    private NvCVImage* _d3dIn, _d3dOut, _bufIn, _bufOut, _tmp;
    private int _inW, _inH, _outW, _outH;
    private bool _failed;

    public string Id => "vfx-vsr";
    public string Status { get; private set; } = "";

    public static IUpscaler? TryCreate(GraphicsDevice g, string quality, bool lossySource)
    {
        var dir = VfxRuntime.FindFor("vfx-vsr");
        if (dir == null)
        {
            Log.Warning("RTX Video is not downloaded");
            return null;
        }
        if (!g.IsNvidia) return null;
        try
        {
            return new VfxVsrUpscaler(g, dir, quality, lossySource);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "RTX Video failed to start");
            return null;
        }
    }

    private VfxVsrUpscaler(GraphicsDevice g, string dir, string quality, bool lossySource)
    {
        _g = g;
        int level = quality switch { "low" => 0, "medium" => 1, "ultra" => 3, _ => 2 };
        // Standard models 1 to 4, HighBitrate models 16 to 19.
        _quality = (uint)(lossySource ? 1 + level : 16 + level);
        _deblur = (uint)(12 + level);
        _label = $"{char.ToUpperInvariant(quality[0])}{quality[1..]}, {(lossySource ? "compressed source" : "clean source")}";
        _copy = new ShaderUpscaler(g, "point");
        _fallback = new ShaderUpscaler(g, "bicubic");
        _fx = new VfxEffect(dir, "VideoSuperRes", "RTX Video");
        Log.Information("RTX Video from {Dir}, quality level {Level} ({Label})", dir, _quality, _label);
    }

    /// <summary>Loads the model for the expected sizes before the first frame; loading takes about half a second.</summary>
    public void Prepare(int inW, int inH, int outW, int outH)
    {
        bool fits = outW >= inW && outH >= inH && inW >= MinW && inH >= MinH && inW <= MaxW && inH <= MaxH;
        if (!fits || _failed) return;
        try { Setup(inW, inH, outW, outH); }
        catch (Exception ex)
        {
            Log.Error(ex, "RTX Video failed to load; bicubic from now on");
            _failed = true;
            Status = "Failed, bicubic in use: " + ex.Message;
        }
    }

    private readonly List<double> _callMs = new();

    public void Process(Ingest ingest, RenderTexture output)
    {
        long start = System.Diagnostics.Stopwatch.GetTimestamp();
        ProcessCore(ingest, output);
        if (_callMs.Count < 100_000) _callMs.Add(System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds);
    }

    private void ProcessCore(Ingest ingest, RenderTexture output)
    {
        var src = ingest.Output!;
        bool upscales = output.Width >= src.Width && output.Height >= src.Height;
        bool inRange = src.Width >= MinW && src.Height >= MinH && src.Width <= MaxW && src.Height <= MaxH;
        if (_failed || !upscales || !inRange || output.Format != Vortice.DXGI.Format.R8G8B8A8_UNorm)
        {
            Status = _failed ? Status : !upscales ? "Output is smaller than the source, bicubic in use" : !inRange ? $"{src.Width} x {src.Height} is outside 640 x 360 to 3840 x 2160, bicubic in use" : "Bicubic in use";
            _fallback.Process(ingest, output);
            return;
        }
        try
        {
            if (_in == null || src.Width != _inW || src.Height != _inH || output.Width != _outW || output.Height != _outH)
                Setup(src.Width, src.Height, output.Width, output.Height);
            _copy.Process(ingest, _in!);
            _fx.Map(_d3dIn);
            _fx.Map(_d3dOut);
            try
            {
                _fx.Transfer(_d3dIn, _bufIn, _tmp);
                _fx.Run();
                _fx.Transfer(_bufOut, _d3dOut, _tmp);
            }
            finally
            {
                _fx.Unmap(_d3dOut);
                _fx.Unmap(_d3dIn);
            }
            _g.Context.CopyResource(output.Texture, _out!.Texture);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "RTX Video failed; bicubic from now on");
            _failed = true;
            Status = "Failed, bicubic in use: " + ex.Message;
            _fallback.Process(ingest, output);
        }
    }

    private void Setup(int inW, int inH, int outW, int outH)
    {
        Release();
        (_inW, _inH, _outW, _outH) = (inW, inH, outW, outH);
        _in = new RenderTexture(_g.Device, inW, inH);
        _out = new RenderTexture(_g.Device, outW, outH);
        _d3dIn = _fx.FromTexture(_in.Texture);
        _d3dOut = _fx.FromTexture(_out.Texture);
        _bufIn = _fx.GpuImage(inW, inH);
        _bufOut = _fx.GpuImage(outW, outH);
        _tmp = VfxEffect.NewImage();

        var started = System.Diagnostics.Stopwatch.StartNew();
        _fx.SetU32("ImageEncodingMode", 0);
        _fx.SetImage("SrcImage0", _bufIn);
        _fx.SetImage("DstImage0", _bufOut);
        _fx.SetStream();
        bool same = inW == outW && inH == outH;
        _fx.SetU32("QualityLevel", same ? _deblur : _quality);
        _fx.SetF32("Strength", 1f);
        _fx.Load();
        _fx.Run();
        Status = same ? "Deblur at source size" : _label;
        Log.Information("RTX Video loaded for {InW} x {InH} to {OutW} x {OutH}, quality level {Level}, in {Ms} ms", inW, inH, outW, outH, same ? _deblur : _quality, started.ElapsedMilliseconds);
    }

    private void Release()
    {
        _fx.Free(ref _d3dIn);
        _fx.Free(ref _d3dOut);
        _fx.Free(ref _bufIn);
        _fx.Free(ref _bufOut);
        _fx.Free(ref _tmp);
        _in?.Dispose();
        _out?.Dispose();
        _in = _out = null;
    }

    public void Dispose()
    {
        if (_callMs.Count > 0)
        {
            var sorted = _callMs.Order().ToList();
            Log.Information("RTX Video: {Count} frames, CPU time per call p50 {P50:F2} ms, p99 {P99:F2} ms, max {Max:F2} ms",
                sorted.Count, sorted[sorted.Count / 2], sorted[(int)(sorted.Count * 0.99)], sorted[^1]);
        }
        Release();
        _fx.Dispose();
        _copy.Dispose();
        _fallback.Dispose();
    }
}
