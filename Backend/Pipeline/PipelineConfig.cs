using TretioDLSS.Output;

namespace TretioDLSS.Pipeline;

public enum PacingMode
{
    Immediate,
    Clocked,
}

/// <summary>Immutable snapshot of everything the pipeline needs. The pipeline never reads Settings.</summary>
public sealed record PipelineConfig
{
    public required Func<IVideoSource> SourceFactory { get; init; }
    public string Upscaler { get; init; } = "bilinear";
    public string UpscaleQuality { get; init; } = "high";
    public string FrameGen { get; init; } = "off";
    public int Multiplier { get; init; } = 1;
    public string FrameGenModel { get; init; } = "medium";
    public Engines.NeuralConfig Neural { get; init; } = Engines.NeuralConfig.Off;
    public double Sharpness { get; init; } = 0.2;
    public string SizeMode { get; init; } = "display";
    public ColorSpec Color { get; init; } = new("auto", "auto");
    public string CadenceMode { get; init; } = "auto";
    public double FixedFps { get; init; } = 30;
    public PacingMode Pacing { get; init; } = PacingMode.Immediate;
    public SyncMode Sync { get; init; } = SyncMode.Vrr;
    public bool Overlay { get; init; }
    public string Adapter { get; init; } = "auto";

    /// <summary>A custom output, for example the TV stream; takes precedence over Monitor.</summary>
    public Func<Graphics.GraphicsDevice, OutputSink>? SinkFactory { get; init; }

    /// <summary>Null means headless: an offscreen sink of this size.</summary>
    public Platform.MonitorInfo? Monitor { get; init; }
    public bool Borderless { get; init; } = true;
    public bool NoActivate { get; init; }
    public string WindowTitle { get; init; } = "TretioDLSS output";

    /// <summary>SDR to HDR output; null means SDR. Used only when the display is in HDR mode, or when forced.</summary>
    public Output.HdrConfig? Hdr { get; init; }

    /// <summary>Headless test runs: HDR output on the offscreen target without an HDR display.</summary>
    public bool ForceHdr { get; init; }

    /// <summary>Window sources: crop black bars around the picture.</summary>
    public bool TrimBlackBars { get; init; }
    public int OffscreenWidth { get; init; } = 2560;
    public int OffscreenHeight { get; init; } = 1440;

    public bool RecordPresents { get; init; }
    public Func<int, string?>? DumpPathFor { get; init; }

    /// <summary>Headless runs: source frames beyond this count are ignored (0 = no limit).</summary>
    public long FrameLimit { get; init; }
    public bool PreviewEnabled { get; init; } = true;
    public bool LossySource { get; init; }
}
