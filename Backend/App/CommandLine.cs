using System.Globalization;

namespace TretioDLSS.App;

/// <summary>Command-line switches.</summary>
public sealed class CommandLine
{
    public bool Headless { get; private set; }
    public string? Source { get; private set; }
    public string? Upscale { get; private set; }
    public string UpscaleQuality { get; private set; } = "high";
    public string? InstallTo { get; private set; }
    public string? FrameGen { get; private set; }
    public string FrameGenModel { get; private set; } = "medium";
    public int Multiplier { get; private set; } = 2;
    public string Neural { get; private set; } = "off";
    public int Layers { get; private set; } = 1;
    public string NeuralStyle { get; private set; } = "default";
    public double NeuralIntensity { get; private set; } = 1;
    public string NeuralModel { get; private set; } = "fast";
    public string NeuralAt { get; private set; } = "source";
    public int Frames { get; private set; } = 600;
    public string? Report { get; private set; }
    public string? Output { get; private set; }
    public (int Index, string Path)? DumpFrame { get; private set; }
    public (int From, int To, string Dir)? DumpFrames { get; private set; }
    public string? PatternWindow { get; private set; }
    /// <summary>Runs the picture for the app over this pipe (the game-named exe).</summary>
    public string? PipelineHost { get; private set; }
    public bool Start { get; private set; }
    public bool Stop { get; private set; }
    public bool Exit { get; private set; }
    public string? Profile { get; private set; }
    public bool CaptureInfo { get; private set; }
    public string? Install { get; private set; }
    public (double Seconds, string Dir)? RecordRaw { get; private set; }
    public string? Api { get; private set; }
    public bool LatencyTest { get; private set; }
    public string? Sync { get; private set; }
    public string? Adapter { get; private set; }
    public string? Pacing { get; private set; }
    public double Sharpness { get; private set; } = -1;
    public double JitterMs { get; private set; }
    public double DropRate { get; private set; }
    public double RepeatNoise { get; private set; }
    public int SceneCutEvery { get; private set; }
    public string? Size { get; private set; }
    public bool ShowOverlay { get; private set; }
    public string? DeviceName { get; private set; }
    public string? CaptureFormat { get; private set; }
    public string? DecodeDumps { get; private set; }
    public double AudioTestSeconds { get; private set; }
    public string Hdr { get; private set; } = "auto";
    public string AudioIn { get; private set; } = "loopback";
    public bool Minimized { get; private set; }
    public double StreamHostSeconds { get; private set; }
    public string? Pin { get; private set; }
    public bool TvScan { get; private set; }
    public List<string> Unknown { get; } = new();

    public bool IsToolMode => PipelineHost != null || Headless || StreamHostSeconds > 0 || TvScan || PatternWindow != null || CaptureInfo || RecordRaw != null || LatencyTest || Install != null;

    /// <summary>Paths are relative to where the user started the exe; the app changes its directory later.</summary>
    private void ResolvePaths()
    {
        static string Full(string p) => Path.GetFullPath(p);
        if (Report != null) Report = Full(Report);
        if (InstallTo != null) InstallTo = Full(InstallTo);
        if (DecodeDumps != null) DecodeDumps = Full(DecodeDumps);
        if (DumpFrame is { } one) DumpFrame = (one.Index, Full(one.Path));
        if (DumpFrames is { } many) DumpFrames = (many.From, many.To, Full(many.Dir));
        if (RecordRaw is { } rec) RecordRaw = (rec.Seconds, Full(rec.Dir));
        if (Source is string src)
        {
            if (src.StartsWith("raw:")) Source = "raw:" + Full(src[4..]);
            else if (src.StartsWith("file:"))
            {
                string path = src[5..], suffix = "";
                int at = path.LastIndexOf('@');
                if (at > 2) { suffix = path[at..]; path = path[..at]; }
                Source = "file:" + Full(path) + suffix;
            }
        }
    }

    public static CommandLine Parse(string[] args)
    {
        var c = new CommandLine();
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{a} needs a value");
            double NextD() => double.Parse(Next(), CultureInfo.InvariantCulture);
            switch (a)
            {
                case "--headless": c.Headless = true; break;
                case "--pipeline-host": c.PipelineHost = Next(); break;
                case "--source": c.Source = Next(); break;
                case "--upscale": c.Upscale = Next(); break;
                case "--vsr-quality": c.UpscaleQuality = Next(); break;
                case "--to": c.InstallTo = Next(); break;
                case "--framegen": c.FrameGen = Next(); break;
                case "--multiplier": c.Multiplier = int.Parse(Next()); break;
                case "--fg-model": c.FrameGenModel = Next(); break;
                case "--neural": c.Neural = Next(); break;
                case "--layers": c.Layers = int.Parse(Next()); break;
                case "--nr-style": c.NeuralStyle = Next(); break;
                case "--nr-intensity": c.NeuralIntensity = NextD(); break;
                case "--nr-model": c.NeuralModel = Next(); break;
                case "--nr-at": c.NeuralAt = Next(); break;
                case "--frames": c.Frames = int.Parse(Next()); break;
                case "--report": c.Report = Next(); break;
                case "--output": c.Output = Next(); break;
                case "--dump-frame": c.DumpFrame = (int.Parse(Next()), Next()); break;
                case "--dump-frames":
                {
                    var range = Next().Split('-');
                    c.DumpFrames = (int.Parse(range[0]), int.Parse(range[^1]), Next());
                    break;
                }
                case "--pattern-window": c.PatternWindow = Next(); break;
                case "--start": c.Start = true; break;
                case "--stop": c.Stop = true; break;
                case "--exit": c.Exit = true; break;
                case "--profile": c.Profile = Next(); break;
                case "--capture-info": c.CaptureInfo = true; break;
                case "--install": c.Install = Next(); break;
                case "--record-raw": c.RecordRaw = (NextD(), Next()); break;
                case "--api": c.Api = Next(); break;
                case "--latency-test": c.LatencyTest = true; break;
                case "--sync": c.Sync = Next(); break;
                case "--adapter": c.Adapter = Next(); break;
                case "--pacing": c.Pacing = Next(); break;
                case "--sharpness": c.Sharpness = NextD(); break;
                case "--jitter": c.JitterMs = NextD(); break;
                case "--drop-rate": c.DropRate = NextD(); break;
                case "--repeat-noise": c.RepeatNoise = NextD(); break;
                case "--scene-cut-every": c.SceneCutEvery = int.Parse(Next()); break;
                case "--size": c.Size = Next(); break;
                case "--overlay": c.ShowOverlay = true; break;
                case "--device": c.DeviceName = Next(); break;
                case "--capture-format": c.CaptureFormat = Next(); break;
                case "--minimized": c.Minimized = true; break;
                case "--decode-dumps": c.DecodeDumps = Next(); break;
                case "--audio-test": c.AudioTestSeconds = NextD(); break;
                case "--hdr": c.Hdr = Next(); break;
                case "--audio-in": c.AudioIn = Next(); break;
                case "--stream-host": c.StreamHostSeconds = NextD(); break;
                case "--pin": c.Pin = Next(); break;
                case "--tv-scan": c.TvScan = true; break;
                default: c.Unknown.Add(a); break;
            }
        }
        c.ResolvePaths();
        return c;
    }
}
