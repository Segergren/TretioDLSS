using System.Text.Json;
using FlashCap;
using NAudio.CoreAudioApi;
using Serilog;
using TretioDLSS.Engines;
using TretioDLSS.App;
using TretioDLSS.Audio;
using TretioDLSS.Capture;
using TretioDLSS.Core;
using TretioDLSS.Nvidia;
using TretioDLSS.Platform;

namespace TretioDLSS.Diagnostics;

/// <summary>Command-line tools.</summary>
public static class Tools
{
    private static readonly JsonSerializerOptions Compact = new(Json.Options) { WriteIndented = false };

    /// <summary>
    /// Runs the audio bridge for a while and checks the buffer level, underruns and drift correction.
    /// Input "loopback" plays back at volume 0, so it is silent.
    /// </summary>
    public static int AudioTest(double seconds, string input, string? reportPath)
    {
        using var bridge = new AudioBridge();
        if (input != "loopback" && !input.StartsWith("{", StringComparison.Ordinal))
            input = AudioDevices.List(NAudio.CoreAudioApi.DataFlow.Capture).FirstOrDefault(d => d.Name.Contains(input, StringComparison.OrdinalIgnoreCase))?.Id ?? input;
        bridge.Start(input, "default");
        if (!bridge.Running)
        {
            Console.Error.WriteLine(bridge.Error);
            return 2;
        }
        bridge.SetVolume(0);
        var levels = new List<double>();
        var balance = new List<(double L, double R)>();
        double maxCorrection = 0;
        var end = DateTime.UtcNow.AddSeconds(seconds);
        Thread.Sleep(2000);
        while (DateTime.UtcNow < end)
        {
            Thread.Sleep(1000);
            levels.Add(bridge.LevelMs);
            var ch = bridge.TakeChannelLevels();
            if (ch.Left > -70 || ch.Right > -70) balance.Add(ch);
            Console.WriteLine($"left {ch.Left:0.0} dB, right {ch.Right:0.0} dB, difference {ch.Right - ch.Left:+0.0;-0.0} dB");
            maxCorrection = Math.Max(maxCorrection, Math.Abs(bridge.Correction - 1));
            if (levels.Count % 60 == 0)
                Log.Information("Audio test {Min} min: level {Level:0.0} ms, underruns {U}, correction {C:0.00000}", levels.Count / 60, bridge.LevelMs, bridge.Underruns, bridge.Correction);
        }
        var report = new
        {
            input = bridge.InputName,
            output = bridge.OutputName,
            seconds,
            targetMs = bridge.TargetMs,
            levelMinMs = levels.Count == 0 ? double.NaN : levels.Min(),
            levelMaxMs = levels.Count == 0 ? double.NaN : levels.Max(),
            levelMedianMs = levels.Count == 0 ? double.NaN : levels.OrderBy(x => x).ElementAt(levels.Count / 2),
            underruns = bridge.Underruns,
            leftDb = balance.Count == 0 ? double.NaN : balance.Average(b => b.L),
            rightDb = balance.Count == 0 ? double.NaN : balance.Average(b => b.R),
            maxCorrectionPercent = maxCorrection * 100,
            pass = levels.Count > 0 && levels.Min() >= 10 && levels.Max() <= 60 && bridge.Underruns == 0 && maxCorrection <= 0.005,
        };
        string json = JsonSerializer.Serialize(report, Json.Options);
        if (reportPath != null) File.WriteAllText(reportPath, json);
        Console.WriteLine(json);
        return report.pass ? 0 : 1;
    }

    /// <summary>Prints index and bar position of every pattern dump in a folder, one JSON object per line.</summary>
    public static int DecodeDumps(string dir, string? size)
    {
        int sw = 1920, sh = 1080;
        if (size?.Split('x') is [var a, var b]) { sw = int.Parse(a); sh = int.Parse(b); }
        var files = Directory.GetFiles(dir, "*.png").OrderBy(f => f, StringComparer.Ordinal).ToList();
        using var output = Console.OpenStandardOutput();
        using var writer = new StreamWriter(output) { AutoFlush = true };
        foreach (var file in files)
        {
            var d = DecodeDump(file, sw, sh);
            writer.WriteLine(JsonSerializer.Serialize(new { file = Path.GetFileName(file), index = d.Index, bar = Math.Round(d.Bar, 2), sobel = Math.Round(d.Sobel, 3), w = d.W, h = d.H }, Compact));
        }
        return files.Count == 0 ? 1 : 0;
    }

    /// <summary>Mean Sobel gradient magnitude of the luma inside a rectangle of a BGRA image.</summary>
    public static double MeanSobel(byte[] bgra, int stride, int x0, int y0, int w, int h)
    {
        double L(int x, int y)
        {
            int p = y * stride + x * 4;
            return 0.0722 * bgra[p] + 0.7152 * bgra[p + 1] + 0.2126 * bgra[p + 2];
        }
        double sum = 0;
        long n = 0;
        for (int y = y0 + 1; y < y0 + h - 1; y++)
            for (int x = x0 + 1; x < x0 + w - 1; x++)
            {
                double gx = L(x + 1, y - 1) + 2 * L(x + 1, y) + L(x + 1, y + 1) - L(x - 1, y - 1) - 2 * L(x - 1, y) - L(x - 1, y + 1);
                double gy = L(x - 1, y + 1) + 2 * L(x, y + 1) + L(x + 1, y + 1) - L(x - 1, y - 1) - 2 * L(x, y - 1) - L(x + 1, y - 1);
                sum += Math.Sqrt(gx * gx + gy * gy);
                n++;
            }
        return n == 0 ? 0 : sum / n;
    }

    public static (long Index, double Bar, double Sobel, int W, int H) DecodeDump(string file, int sourceW, int sourceH)
    {
        using var bmp = new System.Drawing.Bitmap(file);
        var data = bmp.LockBits(new System.Drawing.Rectangle(0, 0, bmp.Width, bmp.Height), System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        try
        {
            var bytes = new byte[data.Stride * bmp.Height];
            System.Runtime.InteropServices.Marshal.Copy(data.Scan0, bytes, 0, bytes.Length);
            int stride = data.Stride;
            (byte, byte, byte) Pixel(int x, int y)
            {
                int p = y * stride + x * 4;
                return (bytes[p + 2], bytes[p + 1], bytes[p]);
            }
            var fit = Output.OutputSink.Fit(sourceW, sourceH, bmp.Width, bmp.Height);
            long index = PatternDecoder.DecodeIndex(Pixel, bmp.Width, bmp.Height, fit.X, fit.Y, fit.W, fit.H, sourceW, sourceH);
            double bar = PatternDecoder.BarPosition(Pixel, bmp.Width, bmp.Height, fit.X, fit.Y, fit.W, fit.H, sourceW, sourceH);
            return (index, bar, MeanSobel(bytes, stride, fit.X, fit.Y, fit.W, fit.H), bmp.Width, bmp.Height);
        }
        finally
        {
            bmp.UnlockBits(data);
        }
    }

    /// <summary>Downloads a Video Effects feature like the UI's download button does.</summary>
    public static int Install(string id, string? to)
    {
        if (to != null) VfxInstaller.TargetOverride = to;
        if (id == "all")
        {
            int worst = 0;
            foreach (var each in VfxInstaller.Ids.ToList()) worst = Math.Max(worst, Install(each, to));
            return worst;
        }
        if (!VfxInstaller.Knows(id))
        {
            Console.WriteLine("Unknown component; use vfx-vfg, vfx-vsr or all");
            return 2;
        }
        Console.WriteLine($"Installing {id} into {VfxInstaller.InstallDir}");
        int last = -1;
        string? error = VfxInstaller.InstallAsync(id, () =>
        {
            var s = VfxInstaller.State(id);
            int pct = (int)(s.Progress * 100);
            if (s.State == "downloading" && pct / 10 != last / 10) Console.WriteLine($"  {pct}% of {s.Bytes / 1e6:F0} MB");
            last = pct;
        }).GetAwaiter().GetResult();
        Console.WriteLine(error ?? "Installed, and the effect loads");
        return error == null ? 0 : 1;
    }

    public static int CaptureInfo()
    {
        var mf = UvcSource.Enumerate();
        var dshow = new List<object>();
        try
        {
            foreach (var d in new FlashCap.CaptureDevices().EnumerateDescriptors().Where(x => x.DeviceType == FlashCap.DeviceTypes.DirectShow))
                dshow.Add(new
                {
                    name = d.Name,
                    formats = d.Characteristics.Select(c => new
                    {
                        width = c.Width,
                        height = c.Height,
                        fps = Math.Round((double)c.FramesPerSecond.Numerator / Math.Max(1, c.FramesPerSecond.Denominator), 3),
                        format = c.PixelFormat.ToString(),
                        raw = c.RawPixelFormat,
                    }),
                });
        }
        catch (Exception ex)
        {
            dshow.Add(new { error = ex.Message });
        }
        var inputs = AudioDevices.List(DataFlow.Capture);
        var privacy = Privacy.Check();
        var info = new
        {
            mediaFoundation = mf.Select(d => new
            {
                id = d.Id,
                name = d.Name,
                defaultFormat = FormatPicker.PickDefault(d.Formats)?.Key,
                matchedAudio = AudioDevices.MatchCaptureCard(inputs, d.Name)?.Name,
                formats = d.Formats.Select(f => f.Key),
            }),
            directShow = dshow,
            catalog = CaptureCatalog.Enumerate().Select(d => new { d.Name, api = d.DshowOnly ? "dshow" : "mf", defaultFormat = FormatPicker.PickDefault(d.Formats)?.Key }),
            audioInputs = inputs.Select(i => i.Name),
            privacy = new { camera = privacy.CameraAllowed ? "allowed" : "blocked", microphone = privacy.MicrophoneAllowed ? "allowed" : "blocked" },
            displays = Monitors.Enumerate().Select(m => new { m.Name, m.Width, m.Height, m.Refresh, m.Vrr, m.Hdr, m.Tv, m.Primary, m.Adapter }),
        };
        Console.WriteLine(JsonSerializer.Serialize(info, Json.Options));
        return 0;
    }

    public static int RecordRaw(CommandLine cli, double seconds, string dir)
    {
        string spec = cli.Source ?? (cli.DeviceName != null ? "uvc:" + cli.DeviceName : "uvc");
        var source = SourceFactory.FromSpec(spec, cli, cli.Api ?? "auto")();
        using var recorder = new RawRecorder(dir, seconds, source.Info, spec);
        source.Start(recorder);
        bool done = recorder.Wait(TimeSpan.FromSeconds(seconds + 10));
        source.Stop();
        source.Dispose();
        int frames = recorder.Frames;
        recorder.Dispose();
        Console.WriteLine(JsonSerializer.Serialize(new { dir, frames, completed = done, source = source.Info.Name }, Json.Options));
        Log.Information("Recorded {Frames} frames to {Dir}", frames, dir);
        return frames > 0 ? 0 : 2;
    }

    public static int LatencyTest(Settings settings, string? source)
    {
        var monitors = Monitors.Enumerate();
        var r = source == "standin"
            ? Diagnostics.LatencyTest.Run(settings, [], monitors, sourceOverride: () => new Diagnostics.LatencyTest.StandInCard(),
                displayOverride: monitors.FirstOrDefault(m => !m.Primary) ?? monitors.FirstOrDefault())
            : Diagnostics.LatencyTest.Run(settings, CaptureCatalog.Enumerate(), monitors);
        Console.WriteLine(JsonSerializer.Serialize(r, Json.Options));
        return r.Error == null ? 0 : 2;
    }
}
