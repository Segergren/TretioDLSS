using System.Buffers;
using TretioDLSS.Pipeline;
using TretioDLSS.Platform;

namespace TretioDLSS.Capture;

public sealed record PatternOptions(
    double ContentFps = 30,
    double ContainerHz = 60,
    PixelLayout Layout = PixelLayout.Nv12,
    int Width = 1920,
    int Height = 1080,
    double RepeatNoise = 0,
    int SceneCutEvery = 0,
    double DropRealProbability = 0,
    double JitterMs = 0,
    bool Black = false,
    int Seed = 1,
    bool Pause = false)
{
    /// <summary>"30in60", optional suffixes like "30in60:yuy2" or "30in60:pause" (still picture 2 s of every 4 s).</summary>
    public static PatternOptions Parse(string spec, int width = 1920, int height = 1080)
    {
        var parts = spec.Split(':');
        var rates = parts[0].Split("in");
        double content = double.Parse(rates[0], System.Globalization.CultureInfo.InvariantCulture);
        double container = rates.Length > 1 ? double.Parse(rates[1], System.Globalization.CultureInfo.InvariantCulture) : content;
        var layout = parts.Skip(1).Where(p => p != "pause").Select(ParseLayout).DefaultIfEmpty(PixelLayout.Nv12).First();
        return new PatternOptions(content, container, layout, width, height, Pause: parts.Contains("pause"));
    }

    public static PixelLayout ParseLayout(string s) => s.ToLowerInvariant() switch
    {
        "yuy2" => PixelLayout.Yuy2,
        "bgra" => PixelLayout.Bgra8,
        _ => PixelLayout.Nv12,
    };
}

/// <summary>
/// Machine-readable test pattern: a binary index strip in the top-left corner and a textured bar
/// whose position is linear in the real-frame index. Rendered on the CPU like a capture card frame,
/// except for the BGRA layout, which the pipeline thread draws on the GPU.
/// </summary>
public sealed class TestPatternSource : IVideoSource
{
    public const int StripBlocks = 24;
    public const int StripBlock = 32;
    public const int BarWidth = 64;
    public const int BarTop = 120;

    private readonly PatternOptions _o;
    private readonly ArrayPool<byte> _pool = ArrayPool<byte>.Create(16 * 1024 * 1024, 8);
    private Thread? _thread;
    private volatile bool _stop;
    private byte[]? _background;
    private byte[]? _last;
    private readonly CpuFrameDiff _diff = new();
    private readonly Random _rng;

    public SourceInfo Info { get; }
    public static float BarStep(int width) => MathF.Max(4, width * 0.0125f);

    /// <summary>Optional diff function that classifies on the source thread (CPU path).</summary>
    public Func<SourceFrame, bool>? Classifier { get; set; }

    public TestPatternSource(PatternOptions options)
    {
        _o = options;
        _rng = new Random(options.Seed);
        Info = new SourceInfo($"Test pattern {options.ContentFps:0.##} fps in {options.ContainerHz:0.##} Hz", "pattern", options.Width, options.Height, options.ContainerHz, options.Layout, options.Layout != PixelLayout.Bgra8);
    }

    public void Start(IFrameSink sink)
    {
        _stop = false;
        _thread = new Thread(() => Run(sink)) { IsBackground = true, Name = "TestPattern", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    public void Stop()
    {
        _stop = true;
        _thread?.Join(2000);
        _thread = null;
    }

    private void Run(IFrameSink sink)
    {
        using var timer = new PreciseTimer();
        double periodMs = 1000.0 / _o.ContainerHz;
        EnsureBackground(_o.Width, _o.Height);
        long start = Qpc.Now;
        long lastContent = -1;
        long realIndex = -1;
        ulong seq = 0;
        sink.OnSourceEvent(SourceEvent.Started);
        for (long tick = 0; !_stop; tick++)
        {
            long due = start + Qpc.FromMs(tick * periodMs);
            if (Qpc.Now - due > Qpc.FromMs(periodMs))
            {
                // Fell behind (a stall): re-anchor instead of firing a burst of late ticks.
                start = Qpc.Now - Qpc.FromMs(tick * periodMs);
                due = Qpc.Now;
            }
            if (_o.JitterMs > 0) due += Qpc.FromMs((_rng.NextDouble() - 0.5) * 2 * _o.JitterMs);
            timer.WaitUntil(due);
            long content = (long)Math.Floor((tick + 1e-9) * _o.ContentFps / _o.ContainerHz);
            bool isNew = content != lastContent;
            if (isNew && _o.DropRealProbability > 0 && lastContent >= 0 && _rng.NextDouble() < _o.DropRealProbability)
                isNew = false; // the game missed its frame time: the previous picture stays one more tick
            if (_o.Pause && lastContent >= 0 && tick * periodMs % 4000 >= 2000) isNew = false;
            if (isNew)
            {
                lastContent = content;
                realIndex++;
            }

            SourceFrame frame;
            if (_o.Layout == PixelLayout.Bgra8)
            {
                frame = new DrawFrame(realIndex, _o.Width, _o.Height) { Diff = isNew ? double.PositiveInfinity : 0 };
            }
            else
            {
                int size = CpuFrame.ByteSize(_o.Layout, _o.Width, _o.Height, out int stride);
                var cf = new CpuFrame(_pool, _o.Layout, _o.Width, _o.Height, stride, size);
                if (isNew || _last == null)
                {
                    Render(cf.Data, realIndex);
                    _last ??= new byte[size];
                    Buffer.BlockCopy(cf.Data, 0, _last, 0, size);
                }
                else
                {
                    Buffer.BlockCopy(_last, 0, cf.Data, 0, size);
                    if (_o.RepeatNoise > 0) AddNoise(cf.Data, size);
                }
                cf.Diff = _diff.Compute(_o.Layout, cf.Data, _o.Width, _o.Height, stride);
                cf.MeanDiff = _diff.LastMean;
                cf.MeanLuma = _diff.LastLuma;
                frame = cf;
            }
            frame.CaptureQpc = Qpc.Now;
            frame.Sequence = seq++;
            sink.OnFrame(frame);
        }
        sink.OnSourceEvent(SourceEvent.Ended);
    }

    private void AddNoise(byte[] data, int size)
    {
        int count = Math.Max(1, (int)(size * 0.002 * _o.RepeatNoise));
        for (int i = 0; i < count; i++)
        {
            int p = _rng.Next(size);
            int v = data[p] + _rng.Next(-1, 2);
            data[p] = (byte)Math.Clamp(v, 0, 255);
        }
    }

    // ---- CPU rendering in limited-range BT.709 YUV ----

    private static (byte Y, byte U, byte V) Yuv(double r, double g, double b)
    {
        double y = 0.2126 * r + 0.7152 * g + 0.0722 * b;
        double u = (b - y) / 1.8556;
        double v = (r - y) / 1.5748;
        return ((byte)Math.Round(16 + 219 * y), (byte)Math.Round(128 + 224 * u), (byte)Math.Round(128 + 224 * v));
    }

    private void EnsureBackground(int w, int h)
    {
        if (_background != null) return;
        // Planar Y + interleaved half-resolution UV (NV12 order) for the background.
        _background = new byte[w * h * 3 / 2];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                double checker = (((x / 16) + (y / 16)) & 1) != 0 ? 0.30 : 0.22;
                double wave = 0.08 * Math.Sin(x * 0.11) * Math.Sin(y * 0.07);
                double r = Math.Clamp(checker * 0.8 + wave, 0, 1), gr = Math.Clamp(checker * 0.6 + wave, 0, 1), b = Math.Clamp(checker + wave, 0, 1);
                var (Y, U, V) = Yuv(r, gr, b);
                _background[y * w + x] = Y;
                if ((x & 1) == 0 && (y & 1) == 0)
                {
                    int ci = w * h + (y / 2) * w + x;
                    _background[ci] = U;
                    _background[ci + 1] = V;
                }
            }
        }
    }

    private void Render(byte[] dst, long index)
    {
        int w = _o.Width, h = _o.Height;
        EnsureBackground(w, h);
        long contentIndex = index;
        if (_o.SceneCutEvery > 0 && index > 0 && index % _o.SceneCutEvery == 0) contentIndex += 1000;
        // Start from the background in NV12 layout.
        var nv12 = _o.Layout == PixelLayout.Nv12 ? dst : ArrayPool<byte>.Shared.Rent(w * h * 3 / 2);
        Buffer.BlockCopy(_background!, 0, nv12, 0, w * h * 3 / 2);
        var on = Yuv(1, 1, 1);
        var off = Yuv(0, 0, 0);
        if (_o.Black)
        {
            Array.Fill(nv12, off.Y, 0, w * h);
            for (int i = w * h; i < w * h * 3 / 2; i += 2) { nv12[i] = 128; nv12[i + 1] = 128; }
        }
        else
        {
            for (int bit = 0; bit < StripBlocks; bit++)
            {
                bool set = ((contentIndex >> (StripBlocks - 1 - bit)) & 1) != 0;
                FillRect(nv12, w, h, bit * StripBlock, 0, StripBlock, StripBlock, set ? on : off);
            }
            int barX = (int)(contentIndex * BarStep(w) % (w - BarWidth));
            var bright = Yuv(1.0, 0.55, 0.2);
            var dark = Yuv(0.75, 0.4125, 0.15);
            for (int y = BarTop; y < h - 60; y += 16)
            {
                FillRect(nv12, w, h, barX, y, BarWidth, 8, dark);
                FillRect(nv12, w, h, barX, y + 8, BarWidth, 8, bright);
            }
        }
        if (_o.Layout == PixelLayout.Yuy2) Nv12ToYuy2(nv12, dst, w, h);
        if (!ReferenceEquals(nv12, dst)) ArrayPool<byte>.Shared.Return(nv12);
    }

    private static void FillRect(byte[] nv12, int w, int h, int x0, int y0, int rw, int rh, (byte Y, byte U, byte V) c)
    {
        int x1 = Math.Min(w, x0 + rw), y1 = Math.Min(h, y0 + rh);
        for (int y = Math.Max(0, y0); y < y1; y++)
            nv12.AsSpan(y * w + x0, x1 - x0).Fill(c.Y);
        for (int y = Math.Max(0, y0) & ~1; y < y1; y += 2)
        {
            int row = w * h + (y / 2) * w;
            for (int x = Math.Max(0, x0) & ~1; x < x1; x += 2)
            {
                nv12[row + x] = c.U;
                nv12[row + x + 1] = c.V;
            }
        }
    }

    private static void Nv12ToYuy2(byte[] nv12, byte[] yuy2, int w, int h)
    {
        for (int y = 0; y < h; y++)
        {
            int src = y * w, crow = w * h + (y / 2) * w, dst = y * w * 2;
            for (int x = 0; x < w; x += 2)
            {
                yuy2[dst + x * 2] = nv12[src + x];
                yuy2[dst + x * 2 + 1] = nv12[crow + x];
                yuy2[dst + x * 2 + 2] = nv12[src + x + 1];
                yuy2[dst + x * 2 + 3] = nv12[crow + x + 1];
            }
        }
    }

    public void Dispose() => Stop();
}

/// <summary>Reads the pattern back from an RGB image (frame dump), for tests.</summary>
public static class PatternDecoder
{
    /// <summary>Real-frame index from the binary strip. The image is the composed output; the strip is scaled.</summary>
    public static long DecodeIndex(Func<int, int, (byte R, byte G, byte B)> pixel, int imageW, int imageH, int contentX, int contentY, int contentW, int contentH, int sourceW, int sourceH)
    {
        double sx = (double)contentW / sourceW, sy = (double)contentH / sourceH;
        long value = 0;
        for (int bit = 0; bit < TestPatternSource.StripBlocks; bit++)
        {
            int x = contentX + (int)((bit * TestPatternSource.StripBlock + TestPatternSource.StripBlock / 2) * sx);
            int y = contentY + (int)(TestPatternSource.StripBlock / 2 * sy);
            var (r, g, b) = pixel(Math.Clamp(x, 0, imageW - 1), Math.Clamp(y, 0, imageH - 1));
            value = (value << 1) | ((r + g + b) / 3 > 128 ? 1L : 0L);
        }
        return value;
    }

    /// <summary>Left edge of the bar, in source pixels, found on a row through its middle.</summary>
    public static double BarPosition(Func<int, int, (byte R, byte G, byte B)> pixel, int imageW, int imageH, int contentX, int contentY, int contentW, int contentH, int sourceW, int sourceH)
    {
        double sx = (double)contentW / sourceW, sy = (double)contentH / sourceH;
        int y = contentY + (int)((sourceH / 2.0) * sy);
        double weighted = 0, total = 0;
        for (int x = contentX; x < contentX + contentW; x++)
        {
            var (r, g, b) = pixel(x, y);
            // Orange: red high, blue low. Weight by how orange the pixel is, to measure blends too.
            double orange = Math.Clamp((r - b - 60) / 120.0, 0, 1);
            weighted += orange * x;
            total += orange;
        }
        if (total < 1) return double.NaN;
        double centerImage = weighted / total;
        return (centerImage - contentX) / sx - TestPatternSource.BarWidth / 2.0;
    }
}
