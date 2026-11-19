using TretioDLSS.Pipeline;

namespace TretioDLSS.Tests;

public class CadenceTests
{
    private const double Change = 12;   // typical moving-picture block difference
    private const double Repeat = 0.0;  // bit-identical repeat

    private static CadenceDetector New(double hz = 60, string mode = "auto", double ceiling = 0.5, double fixedFps = 30) =>
        new(new CadenceOptions(ceiling, mode, hz, fixedFps));

    /// <summary>Feeds a pattern repeatedly and returns the real flags of the last n pushes.</summary>
    private static List<bool> Feed(CadenceDetector d, IEnumerable<double> diffs) => diffs.Select(d.Push).ToList();

    private static IEnumerable<double> Cycle(int n, int count, double change = Change, double repeat = Repeat)
    {
        for (int i = 0; i < count; i++) yield return i % n == 0 ? change : repeat;
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void LocksToEvenCadences(int n)
    {
        var d = New(n == 3 ? 120 : 60);
        var flags = Feed(d, Cycle(n, 600));
        Assert.Equal(CadenceState.Locked, d.State);
        Assert.Equal($"{n}:{n}", d.Label);
        // After lock-in, exactly one real frame per cycle.
        var tail = flags.Skip(300).ToList();
        Assert.Equal(300 / n, tail.Count(f => f));
    }

    [Fact]
    public void NoisyRepeatsStillLock()
    {
        var rng = new Random(3);
        var d = New(ceiling: 2.0);
        var diffs = Enumerable.Range(0, 600).Select(i => i % 2 == 0 ? 8 + rng.NextDouble() * 6 : rng.NextDouble() * 0.6);
        var flags = Feed(d, diffs);
        Assert.Equal("2:2", d.Label);
        Assert.Equal(150, flags.Skip(300).Count(f => f));
    }

    [Fact]
    public void CardScalerNoiseWithSlowMotionLocks()
    {
        var rng = new Random(5);
        var d = New(ceiling: 1.5);
        var diffs = Enumerable.Range(0, 600).Select(i => i % 2 == 0 ? 2.5 + rng.NextDouble() * 1.5 : 0.6 + rng.NextDouble() * 0.2);
        var flags = Feed(d, diffs);
        Assert.Equal("2:2", d.Label);
        Assert.Equal(150, flags.Skip(300).Count(f => f));
    }

    [Fact]
    public void SlowMotionInsideLockStaysReal()
    {
        var d = New();
        Feed(d, Cycle(2, 120));
        // Slow pan: real slots change only a little, still far above the repeat level.
        var flags = Feed(d, Cycle(2, 120, change: 0.9));
        Assert.Equal(CadenceState.Locked, d.State);
        Assert.Equal(60, flags.Count(f => f));
    }

    [Fact]
    public void PhaseSlipMovesPhaseWithoutRelock()
    {
        var d = New();
        Feed(d, Cycle(2, 120));
        long relocks = d.Relocks;
        // The game misses a frame: the real slot repeats, the next tick carries the new picture,
        // and the cadence continues one tick later.
        var seq = new List<double> { Repeat, Change };
        seq.AddRange(Cycle(2, 60).Select((v, i) => i % 2 == 0 ? Repeat : Change));
        var flags = Feed(d, seq);
        Assert.Equal(1, d.Hitches);
        Assert.Equal(relocks, d.Relocks);
        Assert.Equal(CadenceState.Locked, d.State);
        Assert.False(flags[0]);
        Assert.True(flags[1]);
        // Every change after the slip is real, every repeat is not.
        for (int i = 2; i < flags.Count; i++) Assert.Equal(seq[i] > 1, flags[i]);
    }

    [Fact]
    public void StaticPictureHoldsAndResumes()
    {
        var d = New();
        Feed(d, Cycle(2, 120));
        var flags = Feed(d, Enumerable.Repeat(0.0, 40));
        Assert.Equal(CadenceState.Static, d.State);
        Assert.All(flags, f => Assert.False(f));
        // Motion comes back at a new phase.
        var back = Feed(d, Cycle(2, 40).Skip(1));
        Assert.Equal(CadenceState.Locked, d.State);
        Assert.Equal(19, back.Count(f => f));
    }

    [Fact]
    public void VaryingRateNeverLocksButFindsChanges()
    {
        var rng = new Random(5);
        var diffs = new List<double>();
        for (int i = 0; i < 600; i++) diffs.Add(rng.NextDouble() < 0.6 ? Change : Repeat);
        var d = New();
        var flags = Feed(d, diffs);
        Assert.Equal(CadenceState.Unlocked, d.State);
        for (int i = 120; i < diffs.Count; i++) Assert.Equal(diffs[i] > 1, flags[i]);
    }

    [Fact]
    public void CadenceChangeRelocks()
    {
        var d = New();
        Feed(d, Cycle(2, 240));
        Assert.Equal("2:2", d.Label);
        Feed(d, Cycle(1, 240));
        Assert.Equal("1:1", d.Label);
        Feed(d, Cycle(2, 300));
        Assert.Equal("2:2", d.Label);
    }

    [Fact]
    public void SceneCutInRealSlotIsReal()
    {
        var d = New();
        Feed(d, Cycle(2, 120));
        var flags = Feed(d, [80, Repeat, Change, Repeat]);
        Assert.Equal([true, false, true, false], flags);
    }

    [Fact]
    public void OffModeTreatsEveryFrameAsReal()
    {
        var d = New(mode: "off");
        Assert.All(Feed(d, Cycle(2, 100)), f => Assert.True(f));
    }

    [Fact]
    public void FixedModeUsesGivenRate()
    {
        var d = New(mode: "fixed", fixedFps: 30);
        Feed(d, Cycle(2, 120));
        Assert.Equal("2:2", d.Label);
    }

    [Fact]
    public void ThreeTwoPulldownStaysUnlocked()
    {
        var pattern = new[] { Change, Repeat, Repeat, Change, Repeat };
        var d = New();
        var flags = Feed(d, Enumerable.Range(0, 600).Select(i => pattern[i % 5]));
        Assert.Equal(CadenceState.Unlocked, d.State);
        Assert.Equal(240, flags.Skip(0).Count(f => f));
    }
}

public class SourceClockTests
{
    [Fact]
    public void SmoothsArrivalJitter()
    {
        var clock = new SourceClock(33.333);
        var rng = new Random(7);
        var clocked = new List<double>();
        for (int k = 0; k < 400; k++)
        {
            double t = 1000 + k * 33.333 + (rng.NextDouble() - 0.5) * 10;
            clocked.Add(clock.Push(t));
        }
        var intervals = clocked.Zip(clocked.Skip(1), (a, b) => b - a).Skip(100).ToList();
        Assert.True(intervals.Max() - intervals.Min() < 3, $"spread {intervals.Max() - intervals.Min():0.00}");
        Assert.InRange(clock.PeriodMs, 33.0, 33.7);
    }

    [Fact]
    public void MissedFrameCountsAsTwoPeriods()
    {
        var clock = new SourceClock(33.333);
        for (int k = 0; k < 50; k++) clock.Push(k * 33.333);
        double c = clock.Push(51 * 33.333);
        Assert.InRange(c, 51 * 33.333 - 1, 51 * 33.333 + 1);
        Assert.Equal(0, clock.Resyncs);
    }

    [Fact]
    public void ResyncsOnPersistentPhaseChange()
    {
        var clock = new SourceClock(16.667);
        for (int k = 0; k < 20; k++) clock.Push(k * 16.667);
        clock.Push(20 * 16.667 + 8);
        clock.Push(21 * 16.667 + 8);
        Assert.Equal(0, clock.Resyncs);
        double c = clock.Push(22 * 16.667 + 8);
        Assert.Equal(22 * 16.667 + 8, c, 3);
        Assert.Equal(1, clock.Resyncs);
    }

    [Fact]
    public void SingleOutlierDoesNotMoveTheClock()
    {
        var clock = new SourceClock(16.667);
        for (int k = 0; k < 20; k++) clock.Push(k * 16.667);
        double c = clock.Push(20 * 16.667 + 8);
        Assert.Equal(20 * 16.667, c, 3);
        Assert.Equal(21 * 16.667, clock.Push(21 * 16.667), 3);
        Assert.Equal(0, clock.Resyncs);
    }
}

public class ScheduleTests
{
    [Theory]
    [InlineData(2, 33.333)]
    [InlineData(4, 33.333)]
    [InlineData(3, 16.667)]
    public void SlotsAreEvenAcrossIntervals(int n, double period)
    {
        var times = new List<double>();
        for (int k = 0; k < 10; k++)
            for (int j = 1; j <= n; j++)
                times.Add(Schedule.SlotTime(k * period, period, n, 2.0, j));
        var gaps = times.Zip(times.Skip(1), (a, b) => b - a).ToList();
        Assert.All(gaps, g => Assert.Equal(period / n, g, 6));
    }

    [Fact]
    public void HoldIsTheInterpolationPrice()
    {
        Assert.Equal(16.6665, Schedule.HoldMs(33.333, 2), 3);
        Assert.Equal(24.99975, Schedule.HoldMs(33.333, 4), 3);
        Assert.Equal(0, Schedule.HoldMs(33.333, 1));
    }
}

public class NoSignalTests
{
    [Fact]
    public void BlackForTwoSecondsRaisesIt()
    {
        var d = new NoSignalDetector();
        for (int i = 0; i < 119; i++) Assert.False(d.Update(16, i * 16.667));
        Assert.True(d.Update(16, 2000.1));
        Assert.False(d.Update(120, 2017));
    }

    [Fact]
    public void StaticPictureIsNotNoSignal()
    {
        var d = new NoSignalDetector();
        for (int i = 0; i < 600; i++) Assert.False(d.Update(90, i * 16.667));
    }

    [Fact]
    public void BlackAfterAPictureIsContent()
    {
        var d = new NoSignalDetector();
        Assert.False(d.Update(90, 0));
        for (int i = 1; i < 600; i++) Assert.False(d.Update(16, i * 16.667));
    }

    [Fact]
    public void UnmeasuredLumaNeverRaisesIt()
    {
        var d = new NoSignalDetector();
        for (int i = 0; i < 600; i++) Assert.False(d.Update(double.NaN, i * 16.667));
    }
}

public class BlackBarTests
{
    private static byte[] Image(int w, int h, (int X, int Y, int W, int H) picture, byte level = 120)
    {
        var img = new byte[w * h * 4];
        for (int y = picture.Y; y < picture.Y + picture.H; y++)
            for (int x = picture.X; x < picture.X + picture.W; x++)
            {
                int i = (y * w + x) * 4;
                img[i] = img[i + 1] = img[i + 2] = level;
                img[i + 3] = 255;
            }
        return img;
    }

    [Fact]
    public void FindsLetterbox()
    {
        var img = Image(1920, 1080, (0, 140, 1920, 800));
        Assert.Equal((0, 140, 1920, 800), BlackBars.Detect(img, 1920 * 4, 1920, 1080));
    }

    [Fact]
    public void FindsPillarbox()
    {
        var img = Image(1920, 1080, (240, 0, 1440, 1080));
        Assert.Equal((240, 0, 1440, 1080), BlackBars.Detect(img, 1920 * 4, 1920, 1080));
    }

    [Fact]
    public void WindowBorderLinesDoNotHideBars()
    {
        var img = Image(1280, 720, (166, 94, 948, 532));
        for (int x = 0; x < 1280; x++) { int i = (719 * 1280 + x) * 4; img[i] = img[i + 1] = img[i + 2] = 46; }
        for (int y = 0; y < 720; y++) { int i = (y * 1280) * 4; img[i] = img[i + 1] = img[i + 2] = 46; }
        Assert.Equal((166, 94, 948, 532), BlackBars.Detect(img, 1280 * 4, 1280, 720));
    }

    [Fact]
    public void AllBlackKeepsFullFrame() =>
        Assert.Equal((0, 0, 640, 360), BlackBars.Detect(new byte[640 * 360 * 4], 640 * 4, 640, 360));

    [Fact]
    public void ThinEdgesAreIgnored()
    {
        var img = Image(640, 360, (2, 2, 636, 356));
        Assert.Equal((0, 0, 640, 360), BlackBars.Detect(img, 640 * 4, 640, 360));
    }

    [Fact]
    public void SmallBrightAreaIsNotCropped()
    {
        var img = Image(640, 360, (200, 100, 100, 100));
        Assert.Equal((0, 0, 640, 360), BlackBars.Detect(img, 640 * 4, 640, 360));
    }
}
