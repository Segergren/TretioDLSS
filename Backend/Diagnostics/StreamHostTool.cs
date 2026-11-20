using Serilog;
using TretioDLSS.App;
using TretioDLSS.Output;
using TretioDLSS.Pipeline;
using TretioDLSS.Streaming;

namespace TretioDLSS.Diagnostics;

/// <summary>
/// --stream-host: the GameStream host without the window, fed by a test source. Pair and stream with
/// any Moonlight client; --pin answers the pairing prompt on its own.
/// </summary>
public static class StreamHostTool
{
    public static int Run(CommandLine cli, double seconds)
    {
        string spec = cli.Source ?? "pattern:30in60";
        using var host = new GameStreamHost();
        PipelineHost? pipeline = null;
        var gate = new object();
        host.Changed += () =>
        {
            if (cli.Pin != null && host.WaitingForPinFrom is string device)
            {
                Console.WriteLine($"Pairing request from {device}, answering with PIN {cli.Pin}");
                host.SubmitPin(cli.Pin);
            }
        };
        host.StartPipeline = session =>
        {
            lock (gate)
            {
                pipeline?.Dispose();
                var f = session.Format!;
                Console.WriteLine($"Session from {session.Launch.ClientName}: {f.Width}x{f.Height}@{f.Fps}, codec {f.Codec}, {f.BitrateKbps} kbps");
                pipeline = new PipelineHost(new PipelineConfig
                {
                    SourceFactory = SourceFactory.FromSpec(spec, cli, cli.Api ?? "auto"),
                    Upscaler = cli.Upscale ?? "bilinear",
                    FrameGen = cli.FrameGen ?? "off",
                    Multiplier = cli.Multiplier,
                    FrameGenModel = cli.FrameGenModel,
                    Pacing = PacingMode.Clocked,
                    Adapter = cli.Adapter ?? "auto",
                    SinkFactory = host.CreateSink,
                    PreviewEnabled = false,
                });
                pipeline.Start();
            }
        };
        host.StopPipeline = () =>
        {
            lock (gate)
            {
                pipeline?.Dispose();
                pipeline = null;
            }
        };
        host.Start();
        if (!host.Running)
        {
            Console.Error.WriteLine("GameStream host did not start: " + host.Error);
            return 2;
        }
        Console.WriteLine($"GameStream host \"{GameStreamHost.HostName}\" on port {host.Ports!.Http} for {seconds:0} s, source {spec}");
        // A quiet 440 Hz tone as the stream's sound, in 5 ms blocks.
        var tone = new float[4800 * 2];
        long sample = 0;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        using var toneTimer = new Timer(_ =>
        {
            long target = clock.ElapsedTicks * 48000 / System.Diagnostics.Stopwatch.Frequency;
            int n = (int)Math.Min(4800, target - sample);
            for (int i = 0; i < n; i++, sample++)
                tone[i * 2] = tone[i * 2 + 1] = 0.02f * MathF.Sin(2 * MathF.PI * 440 * sample / 48000f);
            if (n > 0) host.OnAudio(tone.AsSpan(0, n * 2), 48000);
        }, null, 0, 5);
        var end = DateTime.UtcNow.AddSeconds(seconds);
        long lastFrames = 0;
        while (DateTime.UtcNow < end)
        {
            Thread.Sleep(1000);
            var t = host.Transport;
            if (t == null || host.Session == null) continue;
            long frames = t.VideoFrames;
            if (frames != lastFrames)
                Console.WriteLine($"video {frames} frames ({frames - lastFrames}/s), {t.VideoPackets} packets, audio {t.AudioPackets} packets");
            lastFrames = frames;
        }
        var tr = host.Transport;
        Log.Information("Stream host tool done: video {Frames} frames, {Packets} packets", tr?.VideoFrames, tr?.VideoPackets);
        Console.WriteLine($"Done: video {tr?.VideoFrames} frames, {tr?.VideoPackets} packets");
        return 0;
    }
}
