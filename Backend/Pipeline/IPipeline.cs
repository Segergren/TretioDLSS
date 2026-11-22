using TretioDLSS.Engines;
using TretioDLSS.Output;

namespace TretioDLSS.Pipeline;

/// <summary>A running picture as the app sees it: in this process, or in the game-named process.</summary>
public interface IPipeline : IDisposable
{
    PipelineStatus Status { get; }
    string? StatusMessage { get; }
    event Action? StatusChanged;
    event Action<byte[]>? Preview;
    void Start();

    void SetPreviewFps(double fps);
    void SetWindowIcon(string? path);
    void SetWindowTitle(string title);
    void SetSharpness(double value);
    void SetOverlay(bool visible);
    void SetHdrLook(HdrConfig look);
    bool SetNeural(NeuralConfig cfg);
    void ToggleCompare();

    PipelineCounters Counters { get; }
    CadenceState CadenceState { get; }
    double ClockPeriodMs { get; }
    (double Source, double Real, double Output) LastRates { get; set; }
    double MedianDelayMs();
    SourceInfo? SourceInfo { get; }
    string CadenceLabel { get; }
    long Hitches { get; }
    long Relocks { get; }
    (int W, int H) ContentSize { get; }
    double StageMean(int stage);
    bool CompareMode { get; }
    string UpscalerStatus { get; }
    string NeuralLine();
    bool NoSignal { get; }

    /// <summary>NVIDIA Smooth Motion's present layer is loaded into the process that presents.</summary>
    bool SmoothMotionActive { get; }

    /// <summary>The exe that runs the picture, as the NVIDIA app lists it.</summary>
    string ExeName { get; }
}
