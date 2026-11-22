using System.Numerics;
using System.Runtime.CompilerServices;

namespace TretioDLSS.Pipeline;

/// <summary>
/// Sparse luma difference between consecutive CPU frames: every 8th pixel of every 8th row,
/// grouped in a 16 x 9 grid of blocks. Returns the largest block mean, in 8-bit steps, so a
/// change in one corner of the picture (a HUD, a small movement) still counts.
/// </summary>
public sealed class CpuFrameDiff
{
    public const int GridX = 16;
    public const int GridY = 9;
    private const int Step = 8;

    private byte[] _prev = [];
    private byte[] _cur = [];
    private int _w, _h;
    private readonly double[] _blockSum = new double[GridX * GridY];
    private readonly int[] _blockCount = new int[GridX * GridY];

    public void Reset() => _w = 0;

    /// <summary>Whole-frame mean difference of the last call; a scene cut shows as a high mean.</summary>
    public double LastMean { get; private set; }

    /// <summary>Mean sampled luma of the last frame, in 8-bit code values.</summary>
    public double LastLuma { get; private set; } = double.NaN;

    /// <summary>Mean difference above which the change counts as a scene cut.</summary>
    public const double SceneCutMean = 35;

    public double Compute(PixelLayout layout, byte[] data, int width, int height, int stride)
    {
        int sw = width / Step, sh = height / Step;
        if (sw != _w || sh != _h)
        {
            _w = sw;
            _h = sh;
            _prev = new byte[sw * sh];
            _cur = new byte[sw * sh];
            Sample(layout, data, stride, _prev);
            return double.PositiveInfinity;
        }
        Sample(layout, data, stride, _cur);
        Array.Clear(_blockSum);
        Array.Clear(_blockCount);
        for (int y = 0; y < sh; y++)
        {
            int by = y * GridY / sh;
            int row = y * sw;
            for (int x = 0; x < sw; x++)
            {
                int bx = x * GridX / sw;
                int b = by * GridX + bx;
                _blockSum[b] += Math.Abs(_cur[row + x] - _prev[row + x]);
                _blockCount[b]++;
            }
        }
        double max = 0, total = 0;
        int count = 0;
        for (int i = 0; i < _blockSum.Length; i++)
        {
            if (_blockCount[i] == 0) continue;
            max = Math.Max(max, _blockSum[i] / _blockCount[i]);
            total += _blockSum[i];
            count += _blockCount[i];
        }
        LastMean = count == 0 ? 0 : total / count;
        long luma = 0;
        foreach (byte v in _cur) luma += v;
        LastLuma = _cur.Length == 0 ? double.NaN : (double)luma / _cur.Length;
        (_prev, _cur) = (_cur, _prev);
        return max;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Sample(PixelLayout layout, byte[] data, int stride, byte[] dst)
    {
        int i = 0;
        for (int y = 0; y < _h; y++)
        {
            int rowStart = y * Step * stride;
            switch (layout)
            {
                case PixelLayout.Nv12:
                    for (int x = 0; x < _w; x++) dst[i++] = data[rowStart + x * Step];
                    break;
                case PixelLayout.Yuy2:
                    for (int x = 0; x < _w; x++) dst[i++] = data[rowStart + x * Step * 2];
                    break;
                default:
                    for (int x = 0; x < _w; x++)
                    {
                        int p = rowStart + x * Step * 4;
                        dst[i++] = (byte)((data[p] * 29 + data[p + 1] * 150 + data[p + 2] * 77) >> 8);
                    }
                    break;
            }
        }
    }
}

public enum CadenceState
{
    Unlocked,
    Locked,
    Static,
}

public sealed record CadenceOptions(
    double RepeatCeiling,
    string Mode = "auto",
    double ContainerHz = 60,
    double FixedFps = 30);

/// <summary>
/// Finds the real frames inside a fixed-refresh signal (for example 30 inside 60).
/// Pure logic over the stream of difference values, one call per container frame.
/// </summary>
public sealed class CadenceDetector
{
    private const int MaxCycle = 4;
    private const int StaticAfter = 8;
    /// <summary>Scaler noise on repeated frames from a capture card (measured up to 0.8).</summary>
    private const double NoiseFloor = 1.0;
    private const double MinNoise = 0.1;
    private const double MaxNoise = 1.2;
    private double _noise;

    private readonly CadenceOptions _o;
    private readonly int _window;
    private readonly Queue<(double Diff, bool RepeatLevel)> _hist = new();
    private long _index;

    private int _cycle;
    private long _phaseOrigin;
    private int _mismatches;
    private int _quietRealSlots;
    private bool _lastRealSlotWasQuiet;
    private long _lastRealSlotIndex = -1;

    public CadenceState State { get; private set; } = CadenceState.Unlocked;
    public int Cycle => State == CadenceState.Unlocked ? 0 : _cycle;
    public long Hitches { get; private set; }
    public long Relocks { get; private set; }

    /// <summary>The last push was a real slot of the locked cadence with an unchanged picture.</summary>
    public bool LastQuietRealSlot { get; private set; }

    /// <summary>Cadence as text, "2:2" style, or empty while unlocked.</summary>
    public string Label => State == CadenceState.Unlocked ? "" : $"{_cycle}:{_cycle}";

    public CadenceDetector(CadenceOptions options)
    {
        _o = options;
        _window = Math.Max(8, (int)Math.Round(options.ContainerHz));
        if (options.Mode == "off")
        {
            State = CadenceState.Locked;
            _cycle = 1;
        }
    }

    /// <summary>Median of the non-repeat differences in the window; infinity when there are none.</summary>
    private double MedianChange()
    {
        var changes = _hist.Where(h => !h.RepeatLevel).Select(h => h.Diff).Where(double.IsFinite).OrderBy(x => x).ToArray();
        return changes.Length == 0 ? double.PositiveInfinity : changes[changes.Length / 2];
    }

    /// <summary>Largest difference a repeat can show: the learned repeat noise with a margin.</summary>
    private double RepeatLimit => Math.Max(MinNoise, _noise * 1.3);

    private bool IsRepeatLevel(double d)
    {
        if (double.IsInfinity(d)) return false;
        if (d >= _o.RepeatCeiling) return false;
        if (d <= RepeatLimit) return true;
        // Once locked, anything above the repeat noise is a new picture, however small the motion.
        if (State != CadenceState.Unlocked) return false;
        double med = MedianChange();
        return !double.IsInfinity(med) && (d * 8 < med || (d < NoiseFloor && d * 3 < med));
    }

    private void LearnNoise(double d)
    {
        if (double.IsFinite(d) && d < _o.RepeatCeiling) _noise = Math.Min(MaxNoise, Math.Max(d, _noise * 0.998));
    }

    /// <summary>Classifies the next container frame. True means it carries a new picture.</summary>
    public bool Push(double diff)
    {
        long i = _index++;
        LastQuietRealSlot = false;
        if (_o.Mode == "off") return true;

        bool repeat = IsRepeatLevel(diff);
        _hist.Enqueue((diff, diff < _o.RepeatCeiling && !double.IsInfinity(diff)));
        while (_hist.Count > _window) _hist.Dequeue();

        if (State == CadenceState.Unlocked)
        {
            TryLock(i);
            if (State == CadenceState.Unlocked) return !repeat;
        }

        long slot = Mod(i - _phaseOrigin, _cycle);
        if (slot != 0 && State == CadenceState.Locked) LearnNoise(diff);
        if (slot == 0)
        {
            _lastRealSlotIndex = i;
            _lastRealSlotWasQuiet = repeat;
            if (repeat)
            {
                // Nothing new to show. Either the game missed a frame (the next tick changes)
                // or the picture is static; neither needs this frame.
                _quietRealSlots++;
                if (_quietRealSlots >= StaticAfter) State = CadenceState.Static;
                LastQuietRealSlot = true;
                return false;
            }
            bool afterGap = _quietRealSlots > 0 && State != CadenceState.Static;
            _quietRealSlots = 0;
            if (State == CadenceState.Static) State = CadenceState.Locked;
            if (afterGap)
            {
                // A short gap of quiet real slots: a missed frame, or the rate dropped.
                Hitches++;
                Mismatch(3);
            }
            else _mismatches = Math.Max(0, _mismatches - 1);
            return true;
        }

        if (repeat) return false;

        // A change in a repeat slot. If the previous real slot was quiet, the game missed a frame:
        // move the phase by one tick and keep the lock.
        if (_lastRealSlotWasQuiet && _lastRealSlotIndex == i - slot && slot == 1 && State != CadenceState.Static)
        {
            _phaseOrigin = i;
            _lastRealSlotIndex = i;
            _lastRealSlotWasQuiet = false;
            _quietRealSlots = 0;
            Hitches++;
            Mismatch(2);
            return true;
        }
        if (State == CadenceState.Static)
        {
            // The picture moves again, at a new phase.
            _phaseOrigin = i;
            _lastRealSlotIndex = i;
            _lastRealSlotWasQuiet = false;
            _quietRealSlots = 0;
            State = CadenceState.Locked;
            return true;
        }
        Mismatch(3);
        return true;
    }

    private void Mismatch(int weight)
    {
        _mismatches += weight;
        if (_mismatches <= _window / 2) return;
        State = CadenceState.Unlocked;
        _mismatches = 0;
        Relocks++;
    }

    private void TryLock(long i)
    {
        if (_hist.Count < _window) return;
        var flags = _hist.Select(h => !IsRepeatLevel(h.Diff)).ToArray(); // true = change
        long start = i - flags.Length + 1;

        if (_o.Mode == "fixed")
        {
            int n = Math.Clamp((int)Math.Round(_o.ContainerHz / Math.Max(1, _o.FixedFps)), 1, MaxCycle);
            int phase = BestPhase(flags, n, out _);
            Lock(n, start + phase);
            return;
        }

        if (flags.All(f => f))
        {
            Lock(1, i);
            return;
        }
        if (flags.All(f => !f)) return;

        for (int n = 2; n <= MaxCycle; n++)
        {
            int phase = BestPhase(flags, n, out int errors);
            if (errors <= Math.Max(1, flags.Length / 20))
            {
                var diffs = _hist.Select(h => h.Diff).ToArray();
                for (int k = 0; k < diffs.Length; k++)
                    if (Mod(k - phase, n) != 0) LearnNoise(diffs[k]);
                Lock(n, start + phase);
                return;
            }
        }
    }

    private static int BestPhase(bool[] changes, int n, out int bestErrors)
    {
        bestErrors = int.MaxValue;
        int best = 0;
        for (int p = 0; p < n; p++)
        {
            int errors = 0;
            for (int k = 0; k < changes.Length; k++)
            {
                bool expected = Mod(k - p, n) == 0;
                if (!expected && changes[k]) errors++;
                if (expected && !changes[k]) errors++;
            }
            if (errors < bestErrors)
            {
                bestErrors = errors;
                best = p;
            }
        }
        return best;
    }

    private void Lock(int cycle, long origin)
    {
        _cycle = cycle;
        _phaseOrigin = origin;
        _mismatches = 0;
        _quietRealSlots = 0;
        State = CadenceState.Locked;
    }

    private static long Mod(long a, long n) => ((a % n) + n) % n;
}

/// <summary>
/// Smoothed clock for real-frame arrivals. Period from the observed intervals, phase corrected
/// with a small gain, hard resync after three arrivals in a row are off by more than 0.45 of a period.
/// </summary>
public sealed class SourceClock
{
    private const double PhaseGain = 0.1;
    private const double PeriodGain = 0.02;
    public const double ResyncFraction = 0.45;
    private const int OutliersToResync = 3;
    private int _outliers;

    private bool _started;
    private double _period;
    private double _clock;
    private readonly double _nominal;

    public double PeriodMs => _period;
    public long Resyncs { get; private set; }

    public SourceClock(double nominalPeriodMs)
    {
        _nominal = nominalPeriodMs;
        _period = nominalPeriodMs;
    }

    /// <summary>Feeds one real-frame arrival time in ms; returns the smoothed time c(k) in ms.</summary>
    public double Push(double arrivalMs)
    {
        if (!_started)
        {
            _started = true;
            _clock = arrivalMs;
            return _clock;
        }
        double elapsed = arrivalMs - _clock;
        int m = Math.Max(1, (int)Math.Round(elapsed / _period));
        double predicted = _clock + m * _period;
        double error = arrivalMs - predicted;
        if (Math.Abs(error) > _period * ResyncFraction)
        {
            // One outlier is jitter and leaves the clock alone; a run of them is a real phase change.
            if (++_outliers < OutliersToResync)
            {
                _clock = predicted;
                return _clock;
            }
            _outliers = 0;
            _clock = arrivalMs;
            Resyncs++;
            return _clock;
        }
        _outliers = 0;
        _clock = predicted + PhaseGain * error;
        _period = Math.Clamp(_period + PeriodGain * error / m, _nominal * 0.5, _nominal * 2);
        return _clock;
    }

    public void SetNominal(double periodMs)
    {
        _period = periodMs;
        _started = false;
    }
}

/// <summary>
/// No-signal state: the picture has been black for a while since the start. Once a picture has
/// been seen, black is content (a loading screen) and never counts.
/// </summary>
public sealed class NoSignalDetector
{
    /// <summary>Limited-range black is 16; a little above allows for card noise.</summary>
    public const double BlackLuma = 24;
    public const double HoldMs = 2000;

    private double _blackSinceMs = double.NaN;
    private bool _sawPicture;

    public bool Active { get; private set; }

    public bool Update(double luma, double nowMs)
    {
        if (!double.IsNaN(luma) && luma > BlackLuma) _sawPicture = true;
        if (_sawPicture || double.IsNaN(luma))
        {
            _blackSinceMs = double.NaN;
            Active = false;
            return Active;
        }
        if (double.IsNaN(_blackSinceMs)) _blackSinceMs = nowMs;
        Active = nowMs - _blackSinceMs >= HoldMs;
        return Active;
    }
}

/// <summary>Present times for one interval of real frame k (pure math).</summary>
public static class Schedule
{
    /// <summary>Slot j = 1..n-1 are generated frames at t = j/n; slot n is the real frame.</summary>
    public static double SlotTime(double clockMs, double periodMs, int n, double allowanceMs, int slot)
        => clockMs + allowanceMs + (slot - 1) * periodMs / n;

    public static double HoldMs(double periodMs, int n) => (n - 1) * periodMs / n;
}
