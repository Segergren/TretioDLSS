using System.Text.Json;
using Serilog;
using TretioDLSS.App;
using TretioDLSS.Capture;
using TretioDLSS.Core;
using TretioDLSS.Nvidia;
using TretioDLSS.Output;
using TretioDLSS.Pipeline;
using TretioDLSS.Platform;

namespace TretioDLSS.Diagnostics;

/// <summary>Runs the pipeline without the UI and writes a JSON report.</summary>
public static class HeadlessRunner
{
    public static int Run(CommandLine cli)
    {
        string spec = cli.Source ?? "pattern:30in60";
        MonitorInfo? monitor = null;
        int offW = 2560, offH = 1440;
        if (cli.Size is string size && size.Contains('x'))
        {
            var parts = size.Split('x');
            offW = int.Parse(parts[0]);
            offH = int.Parse(parts[1]);
        }
        if (cli.Output is string output && output.StartsWith("monitor"))
        {
            var monitors = Monitors.Enumerate();
            int index = output.Contains(':') ? int.Parse(output.Split(':')[1]) : 0;
            monitor = monitors.ElementAtOrDefault(index) ?? monitors.FirstOrDefault(m => m.Primary);
        }

        string fg = cli.FrameGen ?? "off";
        var cfg = new PipelineConfig
        {
            SourceFactory = SourceFactory.FromSpec(spec, cli, cli.Api ?? "auto"),
            Upscaler = cli.Upscale ?? "bilinear",
            UpscaleQuality = cli.UpscaleQuality,
            FrameGen = fg,
            Multiplier = cli.Multiplier,
            FrameGenModel = cli.FrameGenModel,
            Neural = Engines.NeuralConfig.From(cli.Neural, cli.Layers, cli.NeuralStyle, cli.NeuralIntensity, cli.NeuralModel, cli.NeuralAt),
            Sharpness = cli.Sharpness >= 0 ? cli.Sharpness : 0,
            Pacing = cli.Pacing == "clocked" ? PacingMode.Clocked : PacingMode.Immediate,
            Adapter = cli.Adapter ?? "auto",
            Sync = cli.Sync switch { "vsync" => SyncMode.Vsync, "tearing" => SyncMode.Tearing, _ => SyncMode.Vrr },
            Monitor = monitor,
            OffscreenWidth = offW,
            OffscreenHeight = offH,
            Overlay = cli.ShowOverlay,
            RecordPresents = true,
            PreviewEnabled = false,
            LossySource = spec.StartsWith("window"),
            NoActivate = spec.StartsWith("window"),
            TrimBlackBars = spec.StartsWith("window"),
            Hdr = cli.Hdr == "off" ? null : new HdrConfig(),
            ForceHdr = cli.Hdr == "on",
            DumpPathFor = DumpFunc(cli),
            FrameLimit = cli.Frames,
        };

        using var host = new PipelineHost(cfg);
        var started = DateTime.UtcNow;
        try
        {
            host.Start();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("Pipeline did not start: " + ex.Message);
            return 2;
        }
        var deadline = DateTime.UtcNow.AddSeconds(Math.Max(10, cli.Frames / 20.0 + 15));
        while (Interlocked.Read(ref host.Counters.SourceFrames) < cli.Frames && !host.SourceEnded && DateTime.UtcNow < deadline && host.Status is PipelineStatus.Running or PipelineStatus.Waiting)
            Thread.Sleep(5);
        // Let the last interval's frames go out.
        Thread.Sleep(150);
        bool presentLayer = SmoothMotion.PresentLayerLoaded();
        string neuralLine = host.NeuralLine();
        var faulted = host.Status == PipelineStatus.Faulted ? host.StatusMessage : null;
        host.Stop();

        var c = host.Counters;
        var log = host.PresentLog;
        var intervals = log.Zip(log.Skip(1), (a, b) => Qpc.ToMs(b.Qpc - a.Qpc)).ToList();
        double Pct(List<double> v, double p) => v.Count == 0 ? double.NaN : v.OrderBy(x => x).ElementAt(Math.Clamp((int)Math.Round(p * (v.Count - 1)), 0, v.Count - 1));
        double medianInterval = Pct(intervals, 0.5);
        var report = new
        {
            source = spec,
            durationS = (DateTime.UtcNow - started).TotalSeconds,
            cadence = host.CadenceLabel,
            cadenceState = host.CadenceState.ToString(),
            hitches = host.Hitches,
            counts = new
            {
                source = c.SourceFrames,
                real = c.RealFrames,
                repeats = c.Repeats,
                generated = c.Generated,
                quietReal = c.QuietReal,
                held = c.Held,
                presented = c.Presented,
                late = c.Late,
                dropped = c.Dropped,
                sceneCuts = c.SceneCuts,
            },
            stagesGpuMs = host.Stages.Names.Select((n, i) => new { stage = n, p50 = host.Stages.Percentile(i, 0.5), p95 = host.Stages.Percentile(i, 0.95), p99 = host.Stages.Percentile(i, 0.99) }),
            presentIntervalMs = new { p01 = Pct(intervals, 0.01), p50 = Pct(intervals, 0.5), p99 = Pct(intervals, 0.99) },
            longIntervals = intervals.Select((v, i) => (v, i)).Where(x => x.v > medianInterval * 1.5).Select(x => new { present = x.i + 1, ms = Math.Round(x.v, 2) }),
            clockPeriodMs = host.ClockPeriodMs,
            clockResyncs = host.ClockResyncs,
            sourceFps = host.MeasuredSourceFps,
            hdr = new { active = host.HdrActive, dumpMaxNits = host.DumpMaxNits },
            vblanks = VblankReport(host.RefreshHistogram),
            noSignal = new { active = host.NoSignal, firstAfterMs = host.NoSignalSinceStartMs },
            allowanceMs = host.AllowanceMs,
            addedDelayMedianMs = host.MedianDelayMs(),
            generatedIndices = host.GeneratedIndices.Take(2000),
            upscaler = host.EffectiveUpscaler,
            upscalerStatus = host.UpscalerStatus,
            neural = new { config = host.NeuralConfig, status = neuralLine },
            outputSize = new { w = host.OutputSize.W, h = host.OutputSize.H },
            contentSize = new { w = host.ContentSize.W, h = host.ContentSize.H },
            driverSmoothMotionEngaged = presentLayer,
            tearingSupported = host.TearingSupported,
            steadyAllocation = new { bytes = host.SteadyAllocatedBytes, presents = host.SteadyAllocatedFrames },
            faulted,
        };
        string json = JsonSerializer.Serialize(report, Json.Options);
        if (cli.Report != null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(cli.Report))!);
            File.WriteAllText(cli.Report, json);
        }
        Console.WriteLine(json);
        Log.Information("Headless run done: {Real} real, {Gen} generated, {Presented} presented", c.RealFrames, c.Generated, c.Presented);
        return faulted == null ? 0 : 3;
    }

    /// <summary>Repeated and skipped vblanks against the most common refresh count per frame.</summary>
    private static object? VblankReport(long[] h)
    {
        if (h.Length == 0 || h.Sum() == 0) return null;
        int mode = Array.IndexOf(h, h.Max());
        long repeated = 0, skipped = 0;
        for (int i = 0; i < h.Length; i++)
        {
            if (i > mode) repeated += h[i] * (i - mode);
            if (i < mode) skipped += h[i] * (mode - i);
        }
        return new { refreshesPerFrame = mode, histogram = h, repeated, skipped };
    }

    private static Func<int, string?>? DumpFunc(CommandLine cli)
    {
        if (cli.DumpFrame is { } one) return i => i == one.Index ? one.Path : null;
        if (cli.DumpFrames is { } range) return i => i >= range.From && i <= range.To ? Path.Combine(range.Dir, $"frame_{i:D5}.png") : null;
        return null;
    }
}
