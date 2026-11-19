using Serilog;
using TretioDLSS.App;
using TretioDLSS.Capture;
using TretioDLSS.Core;
using TretioDLSS.Graphics;
using TretioDLSS.Output;
using TretioDLSS.Pipeline;
using TretioDLSS.Platform;
using Vortice.Mathematics;

namespace TretioDLSS.Diagnostics;

public sealed record LatencyResult(int Samples, double MinMs, double MedianMs, double MaxMs, string? Error, string? Display = null);

/// <summary>
/// Loopback delay test: a spare GPU output feeds the capture card, so the card
/// shows up as a display. We flash a marker there and time its return through the card.
/// </summary>
public static class LatencyTest
{
    /// <summary>What the test shows right now (0 black, 1 white), for the stand-in card.</summary>
    public static volatile int Shown;

    /// <summary>
    /// Stand-in for the card: a 60 Hz NV12 source whose picture follows what the test
    /// shows, two ticks late. Proves the test runs end to end; its number means nothing.
    /// </summary>
    public sealed class StandInCard : IVideoSource
    {
        private const int W = 1920, H = 1080;
        private Thread? _thread;
        private volatile bool _stop;
        public SourceInfo Info { get; } = new("Stand-in card", "uvc", W, H, 60, PixelLayout.Nv12, true);

        public void Start(IFrameSink sink)
        {
            _stop = false;
            _thread = new Thread(() =>
            {
                var pool = System.Buffers.ArrayPool<byte>.Create(4 * 1024 * 1024, 4);
                using var timer = new PreciseTimer();
                var history = new Queue<int>([0, 0]);
                long start = Qpc.Now;
                for (long tick = 0; !_stop; tick++)
                {
                    timer.WaitUntil(start + Qpc.FromMs(tick * 1000.0 / 60));
                    history.Enqueue(Shown);
                    int level = history.Dequeue();
                    int size = CpuFrame.ByteSize(PixelLayout.Nv12, W, H, out int stride);
                    var f = new CpuFrame(pool, PixelLayout.Nv12, W, H, stride, size);
                    f.Data.AsSpan(0, W * H).Fill(level == 1 ? (byte)235 : (byte)16);
                    f.Data.AsSpan(W * H, W * H / 2).Fill(128);
                    f.CaptureQpc = Qpc.Now;
                    sink.OnFrame(f);
                }
            }) { IsBackground = true, Name = "StandInCard" };
            _thread.Start();
        }

        public void Stop()
        {
            _stop = true;
            _thread?.Join(1000);
        }

        public void Dispose() => Stop();
    }

    private sealed class Watcher : IFrameSink
    {
        public volatile int Level = -1;
        public long LastChangeQpc;
        public readonly AutoResetEvent Changed = new(false);
        public int Expect;

        public void OnFrame(SourceFrame frame)
        {
            using (frame)
            {
                if (frame is not CpuFrame f) return;
                // Mean luma of the center region (marker area).
                long sum = 0;
                int n = 0;
                int y0 = f.Height / 3, y1 = f.Height * 2 / 3, x0 = f.Width / 3, x1 = f.Width * 2 / 3;
                for (int y = y0; y < y1; y += 8)
                for (int x = x0; x < x1; x += 8)
                {
                    sum += f.Layout == PixelLayout.Yuy2 ? f.Data[y * f.Stride + x * 2] : f.Data[y * f.Stride + x];
                    n++;
                }
                int level = (int)(sum / Math.Max(1, n)) > 128 ? 1 : 0;
                if (level != Level)
                {
                    Level = level;
                    LastChangeQpc = f.CaptureQpc;
                    if (level == Expect) Changed.Set();
                }
            }
        }

        public void OnSourceEvent(SourceEvent e, string? message = null) { }
    }

    /// <summary>The display that is most likely the capture card's input.</summary>
    public static MonitorInfo? FindCardDisplay(List<MonitorInfo> monitors, string? cardName)
    {
        var nonPrimary = monitors.Where(m => !m.Primary).ToList();
        string[] marks = ["USB3", "VIDEO", "CAPTURE", "MS2130", "MS2131", "MS2132", "HDMI"];
        return nonPrimary.FirstOrDefault(m => marks.Any(k => m.Name.ToUpperInvariant().Contains(k)))
            ?? (cardName != null ? nonPrimary.FirstOrDefault(m => cardName.Contains(m.Name, StringComparison.OrdinalIgnoreCase)) : null)
            ?? nonPrimary.LastOrDefault();
    }

    public static LatencyResult Run(Settings s, List<CaptureDevice> devices, List<MonitorInfo> monitors, int flashes = 50, Func<IVideoSource>? sourceOverride = null, MonitorInfo? displayOverride = null)
    {
        var dev = devices.FirstOrDefault(d => d.Id == s.Source.DeviceId) ?? devices.FirstOrDefault();
        if (dev == null && sourceOverride == null) return new LatencyResult(0, 0, 0, 0, "No capture card found");
        var display = displayOverride ?? FindCardDisplay(monitors, dev?.Name);
        if (display == null) return new LatencyResult(0, 0, 0, 0, "Connect a spare display output of the PC to the capture card's HDMI input first. The card then shows up as a display.");

        var watcher = new Watcher();
        IVideoSource source;
        try
        {
            source = sourceOverride?.Invoke() ?? SourceFactory.FromSettings(s.Source, devices)();
        }
        catch (Exception ex)
        {
            return new LatencyResult(0, 0, 0, 0, ex.Message);
        }

        var samples = new List<double>();
        GraphicsDevice? g = null;
        OutputWindow? window = null;
        SwapChainSink? sink = null;
        try
        {
            g = GraphicsDevice.Create();
            window = new OutputWindow(display, borderless: true, noActivate: true, "TretioDLSS delay test");
            sink = new SwapChainSink(g, window, SyncMode.Vrr, display.Refresh);
            using var black = new RenderTexture(g.Device, sink.Width, sink.Height);
            using var white = new RenderTexture(g.Device, sink.Width, sink.Height);
            g.Context.ClearRenderTargetView(black.Rtv, new Color4(0, 0, 0, 1));
            g.Context.ClearRenderTargetView(white.Rtv, new Color4(1, 1, 1, 1));
            source.Start(watcher);
            var rng = new Random();
            // Settle on black first.
            watcher.Expect = 0;
            for (int i = 0; i < 20; i++)
            {
                sink.Present(black, default, 0);
                Shown = 0;
                Thread.Sleep(16);
            }
            for (int i = 0; i < flashes; i++)
            {
                bool toWhite = i % 2 == 0;
                watcher.Expect = toWhite ? 1 : 0;
                watcher.Changed.Reset();
                var frame = toWhite ? white : black;
                sink.Present(frame, default, 0);
                long presented = Qpc.Now;
                Shown = toWhite ? 1 : 0;
                if (watcher.Changed.WaitOne(1000))
                {
                    double ms = Qpc.ToMs(watcher.LastChangeQpc - presented);
                    if (ms > 0) samples.Add(ms);
                }
                Thread.Sleep(150 + rng.Next(250));
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Latency test failed");
            return new LatencyResult(samples.Count, 0, 0, 0, "The delay test failed: " + ex.Message, display.Name);
        }
        finally
        {
            try { source.Stop(); } catch { }
            source.Dispose();
            sink?.Dispose();
            window?.Dispose();
            g?.Dispose();
        }
        if (samples.Count < 5) return new LatencyResult(samples.Count, 0, 0, 0, $"Only {samples.Count} flashes came back through the card. Is {display.Name} really the card's input?", display.Name);
        samples.Sort();
        var r = new LatencyResult(samples.Count, samples[0], samples[samples.Count / 2], samples[^1], null, display.Name);
        Log.Information("Delay test on {Display}: {N} samples, min {Min:0.0} ms, median {Median:0.0} ms, max {Max:0.0} ms", display.Name, r.Samples, r.MinMs, r.MedianMs, r.MaxMs);
        return r;
    }
}
