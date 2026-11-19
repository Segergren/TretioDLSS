using Serilog;
using TretioDLSS.Capture;
using TretioDLSS.Graphics;
using TretioDLSS.Output;
using TretioDLSS.Pipeline;
using TretioDLSS.Platform;

namespace TretioDLSS.Diagnostics;

/// <summary>
/// Shows only the test pattern in an ordinary window at a given cadence, as a capture target for
/// window-capture tests (--pattern-window 30in60).
/// </summary>
public static class PatternWindow
{
    public static int Run(string spec)
    {
        var o = PatternOptions.Parse(spec, 1280, 720);
        var monitor = Monitors.Enumerate().FirstOrDefault(m => m.Primary) ?? throw new InvalidOperationException("No display");
        using var g = GraphicsDevice.Create();
        using var window = new OutputWindow(monitor, borderless: false, noActivate: false, $"TretioDLSS pattern {spec}");
        using var sink = new SwapChainSink(g, window, SyncMode.Vrr, monitor.Refresh);
        using var ingest = new Ingest(g);
        using var timer = new PreciseTimer();
        bool closed = false;
        window.CloseRequested += () => closed = true;
        window.EscapePressed += () => closed = true;
        var target = new RenderTexture(g.Device, o.Width, o.Height);
        var scaler = new Engines.ShaderUpscaler(g, "bilinear");
        long start = Qpc.Now;
        long lastContent = -1;
        Log.Information("Pattern window {Spec}", spec);
        for (long tick = 0; !closed; tick++)
        {
            timer.WaitUntil(start + Qpc.FromMs(tick * 1000.0 / o.ContainerHz));
            long content = (long)Math.Floor(tick * o.ContentFps / o.ContainerHz + 1e-9);
            var fit = OutputSink.Fit(o.Width, o.Height, sink.Width, sink.Height);
            bool resized = target.Width != fit.W || target.Height != fit.H;
            if (resized)
            {
                target.Dispose();
                target = new RenderTexture(g.Device, fit.W, fit.H);
            }
            if (content != lastContent || resized)
            {
                lastContent = content;
                ingest.DrawPattern(new DrawFrame(content, o.Width, o.Height), TestPatternSource.BarStep(o.Width));
                scaler.Process(ingest, target);
            }
            // Present every container tick, repeats included, like a console signal.
            sink.Present(target, default, (int)tick);
        }
        target.Dispose();
        scaler.Dispose();
        return 0;
    }
}
