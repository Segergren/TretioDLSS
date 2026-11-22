namespace TretioDLSS.Pipeline;

public sealed class StageTimes
{
    private readonly List<double>[] _samples;
    public string[] Names { get; }

    public StageTimes(params string[] names)
    {
        Names = names;
        _samples = names.Select(_ => new List<double>(SampleCap)).ToArray();
    }

    private const int SampleCap = 20000;

    public void Add(double[] ms)
    {
        lock (this)
        {
            for (int i = 0; i < ms.Length && i < _samples.Length; i++)
            {
                if (double.IsNaN(ms[i])) continue;
                _samples[i].Add(ms[i]);
                if (_samples[i].Count >= SampleCap) _samples[i].RemoveRange(0, SampleCap / 2);
            }
        }
    }

    public double Percentile(int stage, double p)
    {
        lock (this)
        {
            var s = _samples[stage];
            if (s.Count == 0) return double.NaN;
            var sorted = s.OrderBy(x => x).ToArray();
            return sorted[Math.Clamp((int)Math.Round(p * (sorted.Length - 1)), 0, sorted.Length - 1)];
        }
    }

    /// <summary>Recent mean per stage over the last n samples.</summary>
    public double RecentMean(int stage, int n = 30)
    {
        lock (this)
        {
            var s = _samples[stage];
            if (s.Count == 0) return 0;
            return s.Skip(Math.Max(0, s.Count - n)).Average();
        }
    }
}

/// <summary>Counters the pipeline updates; StatsSnapshot is what the UI and reports see.</summary>
public sealed class PipelineCounters
{
    public long SourceFrames;
    public long RealFrames;
    public long Repeats;
    public long QuietReal;
    public long Held;
    public long Generated;
    public long Presented;
    public long Dropped;
    public long Late;
    public long SceneCuts;
}

public sealed record StatsSnapshot(
    double SourceFps,
    double RealFps,
    double OutputFps,
    bool OutputEstimated,
    double AddedDelayMs,
    bool DelayNotMeasured,
    double GpuPercent,
    long Dropped,
    long Late,
    string Cadence,
    double ContainerHz,
    string SourceDetail,
    string UpscaleDetail,
    string FrameGenDetail,
    string OutputDetail,
    double[] HistoryReal,
    double[] HistoryGenerated);
