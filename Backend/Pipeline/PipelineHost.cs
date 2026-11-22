using System.Runtime;
using Serilog;
using TretioDLSS.Engines;
using TretioDLSS.Graphics;
using TretioDLSS.Output;
using TretioDLSS.Platform;

namespace TretioDLSS.Pipeline;

public enum PipelineStatus
{
    Idle,
    Starting,
    Running,
    Stopping,
    Waiting,
    Faulted,
}

/// <summary>
/// Owns the pipeline thread: source handoff, cadence, upscale, frame generation, pacing, present.
/// </summary>
public sealed class PipelineHost : IFrameSink, IDisposable
{
    private readonly PipelineConfig _cfg;
    private Thread? _thread;
    private volatile bool _stop;
    private readonly Mailbox _mailbox = new();
    private readonly ManualResetEventSlim _started = new();
    private IVideoSource? _source;
    private CadenceDetector? _cadence;
    private readonly object _cadenceGate = new();
    private Exception? _startError;

    public PipelineCounters Counters { get; } = new();
    public StageTimes Stages { get; } = new("ingest", "upscale", "framegen", "present");
    public PipelineStatus Status { get; private set; } = PipelineStatus.Idle;
    public string? StatusMessage { get; private set; }
    public event Action? StatusChanged;
    public event Action<byte[]>? Preview;

    public string UpscalerStatus { get; private set; } = "";
    public string CadenceLabel => _cadence?.Label ?? "";
    public CadenceState CadenceState => _cadence?.State ?? CadenceState.Unlocked;
    public long Hitches => _cadence?.Hitches ?? 0;
    public long Relocks => _cadence?.Relocks ?? 0;
    public double ClockPeriodMs { get; private set; }
    public SourceInfo? SourceInfo => _source?.Info;
    public (int W, int H) OutputSize { get; private set; }
    public (int W, int H) ContentSize { get; private set; }
    public double RefreshHz { get; private set; }
    public long ClockResyncs { get; private set; }
    public bool HdrActive { get; private set; }
    public double DumpMaxNits { get; private set; } = double.NaN;

    public void SetHdrLook(HdrConfig look) => _sink?.SetHdrLook(look);

    /// <summary>Size the upscaler renders to; "supersample" renders at twice the display fit and the presenter scales it down.</summary>
    private (int X, int Y, int W, int H) ContentFit(int srcW, int srcH)
    {
        if (_cfg.Upscaler == "off") return OutputSink.Fit(srcW, srcH, _sink!.Width, _sink.Height, "source");
        bool ss = _cfg.SizeMode == "supersample";
        var fit = OutputSink.Fit(srcW, srcH, _sink!.Width, _sink.Height, ss ? "display" : _cfg.SizeMode);
        return ss ? (fit.X, fit.Y, fit.W * 2, fit.H * 2) : fit;
    }

    /// <summary>Refreshes per presented frame in vsync mode, after the run (index 8 = 8 or more).</summary>
    public long[] RefreshHistogram { get; private set; } = [];
    public volatile bool SourceEnded;
    private long _firstSourceQpc, _lastSourceQpc;

    /// <summary>Source frames per second between the first and the last delivered frame.</summary>
    public double MeasuredSourceFps
    {
        get
        {
            long n = Interlocked.Read(ref Counters.SourceFrames);
            double ms = Qpc.ToMs(_lastSourceQpc - _firstSourceQpc);
            return n < 2 || ms <= 0 ? 0 : (n - 1) * 1000.0 / ms;
        }
    }
    private readonly NoSignalDetector _noSignal = new();
    private long _startQpc = Qpc.Now;

    /// <summary>The picture has been black for two seconds (cable, HDCP or console off).</summary>
    public bool NoSignal => _noSignal.Active;

    /// <summary>When the no-signal state was first reached, in ms after start; NaN if never.</summary>
    public double NoSignalSinceStartMs { get; private set; } = double.NaN;

    /// <summary>Managed bytes allocated on the pipeline thread after warm-up, and over how many presents.</summary>
    public long SteadyAllocatedBytes { get; private set; }
    public int SteadyAllocatedFrames { get; private set; }
    private const int AllocWarmup = 120;
    public List<PresentedFrame> PresentLog { get; } = new();
    public List<long> GeneratedIndices { get; } = new(4096);
    public bool TearingSupported { get; private set; }
    public string EffectiveUpscaler { get; private set; } = "";

    private readonly Queue<double> _delays = new();
    private double _allowanceMs = 2.0;
    private OutputSink? _sink;
    private Overlay? _overlay;
    private volatile bool _overlayVisible;
    private volatile bool _compareMode;
    private double _sharpness;

    public PipelineHost(PipelineConfig cfg)
    {
        _cfg = cfg;
        _overlayVisible = cfg.Overlay;
        _sharpness = cfg.Sharpness;
    }

    public void SetOverlay(bool visible) => _overlayVisible = visible;

    private IntPtr _windowHandle;

    public void SetWindowTitle(string title)
    {
        if (_windowHandle == IntPtr.Zero) return;
        SetWindowTextW(_windowHandle, title);
        TretioDLSS.Platform.WindowIdentity.SetName(_windowHandle, title);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern bool SetWindowTextW(IntPtr hwnd, string text);
    public bool OverlayVisible => _overlayVisible;
    public void ToggleCompare() => _compareMode = !_compareMode;
    public bool CompareMode => _compareMode;
    public void SetSharpness(double value) => _sharpness = value;

    private volatile float _previewIntervalMs = 100;

    /// <summary>Preview rate for the app window: up to 30 fps while it is in front, 0 for a still while it is not.</summary>
    public void SetPreviewFps(double fps) => _previewIntervalMs = fps <= 0 ? 0 : (float)(1000 / Math.Min(fps, 30));

    private void SetStatus(PipelineStatus s, string? message = null)
    {
        Status = s;
        StatusMessage = message;
        StatusChanged?.Invoke();
    }

    public void Start()
    {
        SetStatus(PipelineStatus.Starting);
        _stop = false;
        _thread = new Thread(Run) { IsBackground = true, Name = "Pipeline", Priority = ThreadPriority.Highest };
        _thread.Start();
        _started.Wait(10000);
        if (_startError != null) throw _startError;
    }

    public void Stop()
    {
        if (_thread == null) return;
        SetStatus(PipelineStatus.Stopping);
        _stop = true;
        _mailbox.Signal.Set();
        _thread.Join(5000);
        _thread = null;
        if (Status != PipelineStatus.Faulted) SetStatus(PipelineStatus.Idle);
    }

    // ---- source thread side ----

    public void OnFrame(SourceFrame frame)
    {
        if (_cfg.FrameLimit > 0 && Interlocked.Read(ref Counters.SourceFrames) >= _cfg.FrameLimit)
        {
            frame.Dispose();
            return;
        }
        long n = Interlocked.Increment(ref Counters.SourceFrames);
        if (n == 1) _firstSourceQpc = frame.CaptureQpc;
        _lastSourceQpc = frame.CaptureQpc;
        if (!double.IsNaN(frame.MeanLuma))
        {
            bool was = _noSignal.Active;
            if (_noSignal.Update(frame.MeanLuma, Qpc.ToMs(frame.CaptureQpc)) && !was)
            {
                NoSignalSinceStartMs = Qpc.ToMs(Qpc.Now - _startQpc);
                Log.Information("No signal: the picture has been black for {Ms} ms", NoSignalDetector.HoldMs);
            }
        }
        if (frame is CpuFrame)
        {
            // CPU sources classify here, so a repeat never pushes a real frame out of the mailbox.
            bool real, quiet;
            lock (_cadenceGate)
            {
                real = _cadence!.Push(frame.Diff);
                quiet = _cadence.LastQuietRealSlot;
            }
            if (!real)
            {
                Interlocked.Increment(ref Counters.Repeats);
                if (quiet)
                {
                    Interlocked.Exchange(ref _quietQpc, frame.CaptureQpc);
                    _mailbox.Signal.Set();
                }
                frame.Dispose();
                return;
            }
            frame.Real = true;
        }
        long before = Interlocked.Read(ref _mailbox.Replaced);
        _mailbox.Put(frame);
        if (frame is CpuFrame && Interlocked.Read(ref _mailbox.Replaced) != before)
        {
            Interlocked.Increment(ref Counters.Dropped);
            Log.Debug("Dropped a real frame before seq {Seq}", frame.Sequence);
        }
    }

    public void OnSourceEvent(SourceEvent e, string? message = null)
    {
        switch (e)
        {
            case SourceEvent.DeviceLost:
                SetStatus(PipelineStatus.Waiting, message ?? "Waiting for the source to come back");
                break;
            case SourceEvent.DeviceBack:
            case SourceEvent.Started:
                if (Status == PipelineStatus.Waiting) SetStatus(PipelineStatus.Running);
                break;
            case SourceEvent.Error:
                Log.Warning("Source error: {Message}", message);
                SetStatus(PipelineStatus.Waiting, message);
                break;
            case SourceEvent.Ended:
                SourceEnded = true;
                break;
        }
    }

    // Capture time of the last real slot whose picture did not change; 0 when none is waiting.
    private long _quietQpc;

    // ---- pipeline thread ----

    private sealed class Slot
    {
        public long DueQpc;
        public bool Generated;
        public bool Hold;
        public float T;
        public long RealIndex;
        public RenderTexture? Frame;
        public RenderTexture? Prev;
        public RenderTexture? Cur;
        public RenderTexture? Out;
        public bool First;
        public long ArrivalQpc;
        public double ClockMs;
    }

    private void Run()
    {
        GraphicsDevice? g = null;
        Ingest? ingest = null;
        IUpscaler? upscaler = null, compare = null;
        IFrameGenerator? generator = null;
        GpuFrameDiff? gpuDiff = null;
        GpuTimer? timer = null;
        OutputWindow? window = null;
        IntPtr mmcss = IntPtr.Zero;
        var realRing = new RenderTexture?[3];
        RenderTexture? lastGpuFrame = null;
        var preview = new PreviewTap();
        var pending = new List<Slot>(16);
        long realIndex = -1;
        int presentIndex = 0;
        double lastClockMs = double.NaN;
        double holdClockMs = double.NaN;
        long lastRealQpc = 0;
        RenderTexture? prevReal = null;
        bool sceneCut = false;
        var oldLatency = GCSettings.LatencyMode;

        try
        {
            g = GraphicsDevice.Create(_cfg.Adapter);
            g.SetGpuThreadPriority(7);
            uint taskIndex = 0;
            mmcss = Native.AvSetMmThreadCharacteristics("Games", ref taskIndex);
            Native.timeBeginPeriod(1);
            GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency;

            ingest = new Ingest(g) { TrimBlackBars = _cfg.TrimBlackBars };
            gpuDiff = new GpuFrameDiff(g);
            timer = new GpuTimer(g.Device, g.Context, Stages.Names);
            timer.Completed += Stages.Add;

            if (_cfg.SinkFactory != null)
            {
                _sink = _cfg.SinkFactory(g);
            }
            else if (_cfg.Monitor != null)
            {
                window = new OutputWindow(_cfg.Monitor, _cfg.Borderless, _cfg.NoActivate, _cfg.WindowTitle);
                _windowHandle = window.Handle;
                window.CloseRequested += () => { _stop = true; _mailbox.Signal.Set(); };
                window.EscapePressed += () => { if (!_cfg.NoActivate) { _stop = true; _mailbox.Signal.Set(); } };
                var sc = new SwapChainSink(g, window, _cfg.Sync, _cfg.Monitor.Refresh, _cfg.Hdr, _cfg.Monitor.Hdr);
                TearingSupported = sc.TearingSupported;
                _sink = sc;
            }
            else
            {
                _sink = new OffscreenSink(g, _cfg.OffscreenWidth, _cfg.OffscreenHeight, _cfg.ForceHdr ? _cfg.Hdr ?? new HdrConfig() : null);
            }
            _sink.RecordLog = _cfg.RecordPresents;
            _sink.DumpPathFor = _cfg.DumpPathFor;
            RefreshHz = _sink.RefreshHz;
            try
            {
                _overlay = new Overlay(g);
                _sink.Overlay = _overlay;
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Overlay unavailable");
            }

            _source = _cfg.SourceFactory();
            if (_source is Capture.IGpuSource gpuSource) gpuSource.Attach(g);
            var info = _source.Info;
            ingest.SetColor(ResolveColor(_cfg.Color, info));
            _cadence = new CadenceDetector(new CadenceOptions(
                RepeatCeiling: _cfg.LossySource || !info.IsCpu ? 2.0 : 1.5,
                Mode: _cfg.CadenceMode,
                ContainerHz: info.ContainerHz,
                FixedFps: _cfg.FixedFps));
            double containerPeriodMs = 1000.0 / Math.Max(1, info.ContainerHz);
            var clock = new SourceClock(containerPeriodMs);
            int clockCycle = 1;

            SafeMode.Enter([_cfg.Upscaler, _cfg.FrameGen]);
            upscaler = CreateUpscaler(g, _cfg.Upscaler, info);
            EffectiveUpscaler = upscaler.Id;
            compare = new ShaderUpscaler(g, "bilinear");
            generator = CreateGenerator(g, _cfg.FrameGen, _cfg.FrameGenModel, _cfg.Multiplier);
            int n = generator == null ? 1 : FrameGen.ClampMultiplier(generator.Id, _cfg.Multiplier);
            if (generator != null)
            {
                Array.Fill(_genCostMs, generator is BlendGenerator ? 0.1 : 2.0);
                RecordGenCost(_genCostMs[0]);
            }
            bool clocked = _cfg.Pacing == PacingMode.Clocked || n > 1;

            Prewarm(g);
            if (upscaler is VfxVsrUpscaler sdk)
            {
                var fit0 = ContentFit(info.Width, info.Height);
                sdk.Prepare(info.Width, info.Height, fit0.W, fit0.H);
            }
            _startQpc = Qpc.Now;
            _source.Start(this);
            // A source that is waiting (for example a minimized window) reports Running itself later.
            if (Status != PipelineStatus.Waiting) SetStatus(PipelineStatus.Running);
            _started.Set();
            Log.Information("Pipeline running: source {Source}, upscaler {Up}, frame gen {Fg} x{N}, pacing {Pacing}, sync {Sync}",
                info.Name, upscaler.Id, generator?.Id ?? "off", n, clocked ? "clocked" : "immediate", _cfg.Sync);

            using var wait = new PreciseTimer();
            long lastStatsTick = Environment.TickCount64;
            long allocStart = -1;
            int allocStartIndex = 0;
            long runStartTick = Environment.TickCount64;
            bool markerCleared = false;
            while (!_stop)
            {
                long nextDue = long.MaxValue;
                foreach (var ps in pending)
                {
                    long at = ps.Generated && ps.Out == null ? ps.DueQpc - _genLeadQpc : ps.DueQpc;
                    if (at < nextDue) nextDue = at;
                }
                if (nextDue == long.MaxValue) _mailbox.Signal.WaitOne(100);
                else if (nextDue > Qpc.Now) wait.WaitUntil(nextDue, _mailbox.Signal);
                if (_stop) break;

                var frame = _mailbox.Take();
                long quietQpc = Interlocked.Exchange(ref _quietQpc, 0);
                bool timing = frame != null || pending.Count > 0;
                if (timing) timer.BeginFrame();
                if (frame != null)
                {
                    using (frame)
                    {
                        timer.BeginStage(0);
                        RenderTexture? working;
                        if (frame is CpuFrame cf)
                        {
                            ingest.Upload(cf, convert: true);
                            working = ingest.Output;
                        }
                        else if (frame is DrawFrame df)
                        {
                            ingest.DrawPattern(df, Capture.TestPatternSource.BarStep(df.Width));
                            working = ingest.Output;
                        }
                        else
                        {
                            ingest.UploadGpu((GpuFrame)frame);
                            working = ingest.Output;
                        }
                        timer.EndStage();

                        bool isReal = true;
                        if (frame is not CpuFrame)
                        {
                            // GPU sources: compare with the last frame the pipeline consumed.
                            double d = double.PositiveInfinity;
                            if (lastGpuFrame != null && lastGpuFrame.Width == working!.Width && lastGpuFrame.Height == working.Height)
                                d = gpuDiff.Compute(working, lastGpuFrame);
                            lastGpuFrame ??= new RenderTexture(g.Device, working!.Width, working.Height, working.Format);
                            if (lastGpuFrame.Width != working!.Width || lastGpuFrame.Height != working.Height)
                            {
                                lastGpuFrame.Dispose();
                                lastGpuFrame = new RenderTexture(g.Device, working.Width, working.Height, working.Format);
                            }
                            g.Context.CopyResource(lastGpuFrame.Texture, working.Texture);
                            lock (_cadenceGate)
                            {
                                isReal = _cadence.Push(d);
                                if (_cadence.LastQuietRealSlot) quietQpc = frame.CaptureQpc;
                            }
                            if (double.IsFinite(d) && gpuDiff.LastMean > CpuFrameDiff.SceneCutMean) sceneCut = true;
                            if (!isReal) Interlocked.Increment(ref Counters.Repeats);
                        }
                        else if (double.IsFinite(frame.Diff) && frame.MeanDiff > CpuFrameDiff.SceneCutMean)
                        {
                            sceneCut = true;
                        }

                        if (isReal)
                        {
                            Interlocked.Increment(ref Counters.RealFrames);
                            realIndex++;
                            double arrivalMs = Qpc.ToMs(frame.CaptureQpc);
                            int cycle;
                            CadenceState cadenceState;
                            lock (_cadenceGate)
                            {
                                cycle = _cadence.Cycle;
                                cadenceState = _cadence.State;
                            }
                            if (cycle > 0 && cycle != clockCycle)
                            {
                                // The content period is the cadence cycle times the container period.
                                clockCycle = cycle;
                                clock.SetNominal(cycle * containerPeriodMs);
                            }
                            double clockMs = clock.Push(arrivalMs);
                            ClockPeriodMs = clock.PeriodMs;
                            ClockResyncs = clock.Resyncs;
                            lastClockMs = clockMs;
                            holdClockMs = clockMs;
                            lastRealQpc = frame.CaptureQpc;

                            // Output size: fit the content into the sink.
                            int srcW = working!.Width, srcH = working.Height;
                            var fit = ContentFit(srcW, srcH);
                            ContentSize = (fit.W, fit.H);
                            OutputSize = (_sink.Width, _sink.Height);

                            int ringSlot = (int)(realIndex % realRing.Length);
                            var target = realRing[ringSlot];
                            if (target == null || target.Width != fit.W || target.Height != fit.H)
                            {
                                target?.Dispose();
                                target = realRing[ringSlot] = new RenderTexture(g.Device, fit.W, fit.H);
                                generator?.Prepare(fit.W, fit.H);
                                prevReal = null;
                            }

                            timer.BeginStage(1);
                            var active = _compareMode ? compare : upscaler;
                            active.Process(ingest, target);
                            target.Stamp = realIndex + 1;
                            timer.EndStage();
                            if (upscaler.Status.Length > 0 && upscaler.Status != UpscalerStatus) UpscalerStatus = upscaler.Status;

                            // Overrun: generated frames of the previous interval still pending are dropped,
                            // and that interval's real frame goes out now.
                            if (pending.Count > 0)
                            {
                                for (int k = pending.Count - 1; k >= 0; k--)
                                {
                                    if (!pending[k].Generated && !pending[k].Hold) continue;
                                    if (pending[k].Generated) Interlocked.Increment(ref Counters.Late);
                                    ReturnSlot(pending[k]);
                                    pending.RemoveAt(k);
                                }
                                long nowQpc = Qpc.Now;
                                foreach (var ps in pending) ps.DueQpc = nowQpc;
                                PresentDue(pending, generator, timer, wait, g, preview, ref presentIndex, force: true);
                            }

                            double readyMs = Qpc.ToMs(Qpc.Now);
                            UpdateAllowance(readyMs - clockMs);

                            if (n > 1 && prevReal != null && !sceneCut && cadenceState != CadenceState.Unlocked)
                            {
                                // The first generated frame can only start once this real frame is in, so its cost joins the budget.
                                double budgetMs = _allowanceMs + Qpc.ToMs(_genLeadQpc);
                                for (int j = 1; j < n; j++)
                                {
                                    var gs = RentSlot();
                                    gs.DueQpc = Qpc.FromMs(Schedule.SlotTime(clockMs, clock.PeriodMs, n, budgetMs, j));
                                    gs.Generated = true;
                                    gs.T = (float)j / n;
                                    gs.First = j == 1;
                                    gs.RealIndex = realIndex;
                                    gs.Prev = prevReal;
                                    gs.Cur = target;
                                    gs.ArrivalQpc = frame.CaptureQpc;
                                    gs.ClockMs = clockMs;
                                    pending.Add(gs);
                                }
                                var rs = RentSlot();
                                rs.DueQpc = Qpc.FromMs(Schedule.SlotTime(clockMs, clock.PeriodMs, n, budgetMs, n));
                                rs.RealIndex = realIndex;
                                rs.Frame = target;
                                rs.ArrivalQpc = frame.CaptureQpc;
                                rs.ClockMs = clockMs;
                                pending.Add(rs);
                            }
                            else
                            {
                                if (sceneCut)
                                {
                                    generator?.Reset();
                                    Interlocked.Increment(ref Counters.SceneCuts);
                                }
                                long due = clocked ? Qpc.FromMs(clockMs + _allowanceMs) : Qpc.Now;
                                var os = RentSlot();
                                os.DueQpc = due;
                                os.RealIndex = realIndex;
                                os.Frame = target;
                                os.ArrivalQpc = frame.CaptureQpc;
                                os.ClockMs = clockMs;
                                pending.Add(os);
                            }
                            sceneCut = false;
                            prevReal = target;
                        }
                    }
                }

                // A quiet slot older than the last real frame (after a stall) has nothing left to hold.
                if (quietQpc != 0 && quietQpc > lastRealQpc && prevReal != null && double.IsFinite(holdClockMs))
                {
                    // The game sent a frame but nothing changed: show the last picture again on the same clock.
                    Interlocked.Increment(ref Counters.QuietReal);
                    double p = clock.PeriodMs;
                    int m = Math.Max(1, (int)Math.Round((Qpc.ToMs(quietQpc) - holdClockMs) / p));
                    holdClockMs += m * p;
                    CadenceState holdState;
                    lock (_cadenceGate) holdState = _cadence.State;
                    int slots = n > 1 && holdState != CadenceState.Unlocked ? n : 1;
                    double budgetMs = _allowanceMs + (slots > 1 ? Qpc.ToMs(_genLeadQpc) : 0);
                    for (int j = 1; j <= slots; j++)
                    {
                        var hs = RentSlot();
                        hs.DueQpc = slots > 1 ? Qpc.FromMs(Schedule.SlotTime(holdClockMs, p, n, budgetMs, j))
                            : clocked ? Qpc.FromMs(holdClockMs + _allowanceMs) : Qpc.Now;
                        hs.Hold = true;
                        hs.RealIndex = realIndex;
                        hs.Frame = prevReal;
                        hs.ArrivalQpc = quietQpc;
                        hs.ClockMs = holdClockMs;
                        pending.Add(hs);
                    }
                }

                PresentDue(pending, generator, timer, wait, g, preview, ref presentIndex, force: false);
                if (generator != null) GenerateAhead(pending, generator, timer, g);
                if (timing) timer.EndFrame();

                if (!markerCleared && Environment.TickCount64 - runStartTick > SafeMode.StableMs)
                {
                    // Past start-up: a later forced kill or power loss is not a crash inside the engine.
                    SafeMode.Clear();
                    markerCleared = true;
                }

                if (presentIndex >= AllocWarmup)
                {
                    long now = GC.GetAllocatedBytesForCurrentThread();
                    if (allocStart < 0) { allocStart = now; allocStartIndex = presentIndex; }
                    else
                    {
                        SteadyAllocatedBytes = now - allocStart;
                        SteadyAllocatedFrames = presentIndex - allocStartIndex;
                    }
                }

                if (_overlay != null && Environment.TickCount64 - lastStatsTick > 250)
                {
                    lastStatsTick = Environment.TickCount64;
                    if (_overlayVisible) _overlay.SetLines(OverlayLines());
                    _overlay.Visible = _overlayVisible;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Pipeline failed");
            _startError ??= _started.IsSet ? null : ex;
            SetStatus(PipelineStatus.Faulted, ex is DeviceLostException ? "The graphics device was lost" : ex.Message);
        }
        finally
        {
            _started.Set();
            try { _source?.Stop(); } catch (Exception ex) { Log.Warning(ex, "Source stop failed"); }
            _source?.Dispose();
            foreach (var t in realRing) t?.Dispose();
            foreach (var ps in pending) ps.Out?.Dispose();
            while (_genPool.Count > 0) _genPool.Pop().Dispose();
            lastGpuFrame?.Dispose();
            preview.Dispose();
            generator?.Dispose();
            upscaler?.Dispose();
            compare?.Dispose();
            if (_sink is SwapChainSink chain) RefreshHistogram = chain.RefreshHistogram;
            if (_sink != null)
            {
                _sink.FlushDumps();
                HdrActive = _sink.Hdr != null;
                DumpMaxNits = _sink.LastDumpMaxNits;
            }
            if (_sink != null)
            {
                lock (_sink.PresentLog) PresentLog.AddRange(_sink.PresentLog);
                Counters.Presented = Interlocked.Read(ref _sink.Presented);
            }
            _sink?.Dispose();
            _overlay?.Dispose();
            window?.Dispose();
            timer?.Dispose();
            gpuDiff?.Dispose();
            ingest?.Dispose();
            g?.Dispose();
            SafeMode.Clear();
            GCSettings.LatencyMode = oldLatency;
            Native.timeEndPeriod(1);
            if (mmcss != IntPtr.Zero) Native.AvRevertMmThreadCharacteristics(mmcss);
            // Ended from the output window (Esc or its close button), not through Stop: report it.
            if (Status is PipelineStatus.Starting or PipelineStatus.Running or PipelineStatus.Waiting)
            {
                Log.Information("Pipeline ended from the output window");
                SetStatus(PipelineStatus.Idle);
            }
        }
    }

    /// <summary>Compiles every shader the stages use before the first frame, so it is not late.</summary>
    private static void Prewarm(GraphicsDevice g)
    {
        foreach (var (file, entry) in new[]
        {
            ("ingest.hlsl", "PSNv12"), ("ingest.hlsl", "PSYuy2"), ("ingest.hlsl", "PSBgra"),
            ("scale.hlsl", "PSBilinear"), ("scale.hlsl", "PSBicubic"), ("scale.hlsl", "PSBlend"), ("scale.hlsl", "PSPoint"),
            ("fsr.hlsl", "PSEasu"), ("fsr.hlsl", "PSRcas"), ("util.hlsl", "PSCopy"), ("util.hlsl", "PSDiff"), ("util.hlsl", "PSPattern"), ("util.hlsl", "PSHdr"),
        })
        {
            try { g.PixelShader(file, entry); }
            catch (Exception ex) { Log.Warning(ex, "Shader {File}:{Entry} failed to compile", file, entry); }
        }
    }

    // Generated frames start early by the generator's recent worst cost, so they still present on time.
    private long _genLeadQpc;
    private readonly double[] _genCostMs = new double[32];
    private int _genCostNext;

    private void RecordGenCost(double ms)
    {
        _genCostMs[_genCostNext] = ms;
        _genCostNext = (_genCostNext + 1) % _genCostMs.Length;
        _genLeadQpc = Qpc.FromMs(Math.Min(14, _genCostMs.Max() + 1.0));
    }

    private void PresentDue(List<Slot> pending, IFrameGenerator? generator, GpuTimer timer, PreciseTimer wait, GraphicsDevice g, PreviewTap preview, ref int presentIndex, bool force)
    {
        while (pending.Count > 0)
        {
            int ni = 0;
            for (int k = 1; k < pending.Count; k++) if (pending[k].DueQpc < pending[ni].DueQpc) ni = k;
            var next = pending[ni];
            long startAt = next.Generated && generator != null && next.Out == null ? next.DueQpc - _genLeadQpc : next.DueQpc;
            if (!force && startAt > Qpc.Now + Qpc.FromMs(0.05)) break;
            pending.RemoveAt(ni);
            RenderTexture frame;
            if (next.Generated && generator != null && next.Prev != null && next.Cur != null)
            {
                if (next.Out == null) GenerateSlot(next, generator, timer, g, critical: true);
                frame = next.Out!;
                Interlocked.Increment(ref Counters.Generated);
                lock (GeneratedIndices) GeneratedIndices.Add(presentIndex);
            }
            else if (next.Frame != null)
            {
                frame = next.Frame;
            }
            else
            {
                ReturnSlot(next);
                continue;
            }

            _sink!.Sharpness = _sharpness;
            _sink.WaitReady(20);
            if (next.Generated && !force && next.DueQpc > Qpc.Now) wait.WaitUntil(next.DueQpc);
            timer.BeginStage(3);
            _sink.Present(frame, new PresentedFrame(Qpc.Now, next.Generated, next.RealIndex, next.Generated ? next.T : 1), presentIndex++);
            timer.EndStage();
            if (next.Hold) Interlocked.Increment(ref Counters.Held);
            else if (!next.Generated) RecordDelay(Qpc.ToMs(Qpc.Now - next.ArrivalQpc));
            if (_cfg.PreviewEnabled && Preview != null && !next.Generated && !next.Hold) preview.Offer(g, frame, Preview, _previewIntervalMs);
            ReturnSlot(next);
        }
    }

    // Generates the earliest pending frame ahead of its time unless a present is due before it could finish.
    private void GenerateAhead(List<Slot> pending, IFrameGenerator generator, GpuTimer timer, GraphicsDevice g)
    {
        while (!_stop)
        {
            Slot? next = null;
            long nextPresent = long.MaxValue;
            foreach (var ps in pending)
            {
                if (ps.Generated && ps.Out == null)
                {
                    if (ps.Prev != null && ps.Cur != null && (next == null || ps.DueQpc < next.DueQpc)) next = ps;
                }
                else if (ps.DueQpc < nextPresent) nextPresent = ps.DueQpc;
            }
            if (next == null) return;
            if (nextPresent < next.DueQpc && nextPresent - Qpc.Now < _genLeadQpc) return;
            GenerateSlot(next, generator, timer, g, critical: next.First);
        }
    }

    // Only frames on the critical path (generated just in time, or the first of an interval) set the lead.
    private void GenerateSlot(Slot s, IFrameGenerator generator, GpuTimer timer, GraphicsDevice g, bool critical)
    {
        var tex = RentGenTexture(g, s.Cur!.Width, s.Cur.Height);
        timer.BeginStage(2);
        long genStart = Qpc.Now;
        try
        {
            generator.Generate(s.Prev!, s.Cur, s.T, tex);
        }
        catch
        {
            _genPool.Push(tex);
            throw;
        }
        if (critical) RecordGenCost(Qpc.ToMs(Qpc.Now - genStart));
        timer.EndStage();
        s.Out = tex;
    }

    private readonly Stack<RenderTexture> _genPool = new(8);

    private RenderTexture RentGenTexture(GraphicsDevice g, int w, int h)
    {
        while (_genPool.Count > 0)
        {
            var t = _genPool.Pop();
            if (t.Width == w && t.Height == h) return t;
            t.Dispose();
        }
        return new RenderTexture(g.Device, w, h);
    }

    private readonly Stack<Slot> _slotPool = new(16);

    private Slot RentSlot() => _slotPool.Count > 0 ? _slotPool.Pop() : new Slot();

    private void ReturnSlot(Slot s)
    {
        s.Generated = false;
        s.Hold = false;
        s.First = false;
        s.T = 0;
        if (s.Out != null) _genPool.Push(s.Out);
        s.Frame = s.Prev = s.Cur = s.Out = null;
        _slotPool.Push(s);
    }

    private readonly double[] _allowanceRing = new double[120];
    private readonly double[] _allowanceSorted = new double[120];
    private int _allowanceCount, _allowanceNext;

    private void UpdateAllowance(double sample)
    {
        _allowanceRing[_allowanceNext] = Math.Max(0, sample);
        _allowanceNext = (_allowanceNext + 1) % _allowanceRing.Length;
        _allowanceCount = Math.Min(_allowanceCount + 1, _allowanceRing.Length);
        Array.Copy(_allowanceRing, _allowanceSorted, _allowanceCount);
        Array.Sort(_allowanceSorted, 0, _allowanceCount);
        double p95 = _allowanceSorted[(int)Math.Floor(0.95 * (_allowanceCount - 1))];
        // Slow update so one late frame does not move every following slot.
        _allowanceMs = _allowanceMs * 0.9 + (p95 + 0.5) * 0.1;
    }

    public double AllowanceMs => _allowanceMs;

    private void RecordDelay(double ms)
    {
        lock (_delays)
        {
            _delays.Enqueue(ms);
            while (_delays.Count > 120) _delays.Dequeue();
        }
    }

    public double MedianDelayMs()
    {
        lock (_delays)
        {
            if (_delays.Count == 0) return 0;
            var s = _delays.OrderBy(x => x).ToArray();
            return s[s.Length / 2];
        }
    }

    private string[] OverlayLines()
    {
        var info = _source?.Info;
        return
        [
            $"Source\t{info?.Width}x{info?.Height} {info?.ContainerHz:0.##} Hz",
            $"Cadence\t{(string.IsNullOrEmpty(CadenceLabel) ? "unlocked" : CadenceLabel)}",
            $"Real fps\t{LastRates.Real:0}",
            $"Output fps\t{LastRates.Output:0}",
            $"Added delay\t{MedianDelayMs():0.0} ms",
            $"Upscale\t{App.EngineCatalog.Name(EffectiveUpscaler)}{(_compareMode ? " (compare: Bilinear)" : "")}",
            $"GPU upscale\t{Stages.RecentMean(1):0.00} ms",
            $"Dropped / late\t{Counters.Dropped} / {Counters.Late}",
        ];
    }

    public (double Source, double Real, double Output) LastRates { get; set; }

    private static ColorSpec ResolveColor(ColorSpec spec, SourceInfo info)
    {
        string matrix = spec.Matrix == "auto" ? (info.Height >= 720 ? "bt709" : "bt601") : spec.Matrix;
        string range = spec.Range == "auto" ? "limited" : spec.Range;
        return new ColorSpec(matrix, range);
    }

    /// <summary>NIS while RTX Video's files are not on this PC (no download yet, or no NVIDIA GPU).</summary>
    private IUpscaler RtxVideoMissing(GraphicsDevice g)
    {
        Log.Information("RTX Video unavailable, using NIS");
        UpscalerStatus = g.IsNvidia ? "RTX Video not downloaded yet, NIS in use" : "Needs an NVIDIA RTX GPU, NIS in use";
        return new NisUpscaler(g);
    }

    private IUpscaler CreateUpscaler(GraphicsDevice g, string id, SourceInfo info)
    {
        if (SafeMode.IsDisabled(id))
        {
            UpscalerStatus = SafeMode.Message(id);
            Log.Warning("{Message}", UpscalerStatus);
            return new ShaderUpscaler(g, "bilinear");
        }
        try
        {
            return id switch
            {
                "fsr1" => new Fsr1Upscaler(g),
                "bicubic" => new ShaderUpscaler(g, "bicubic"),
                "vfx-vsr" or "rtx-video" => VfxVsrUpscaler.TryCreate(g, _cfg.UpscaleQuality, _cfg.LossySource) ?? RtxVideoMissing(g),
                "nis" => new NisUpscaler(g),
                "off" => new ShaderUpscaler(g, "bilinear"),
                _ => new ShaderUpscaler(g, "bilinear"),
            };
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Upscaler {Id} unavailable, using bilinear", id);
            UpscalerStatus = "Not available, plain scaling in use";
            return new ShaderUpscaler(g, "bilinear");
        }
    }

    private static IFrameGenerator? CreateGenerator(GraphicsDevice g, string id, string model, int multiplier) => SafeMode.IsDisabled(id) ? null : id switch
    {
        "blend" => new BlendGenerator(g),
        "vfx-vfg" => VfxFrameGenerator.TryCreate(g, model),
        "dlss-fg" => DlssFrameGenerator.TryCreate(g, multiplier),
        "rife" => RifeFrameGenerator.TryCreate(g, model),
        "amd-frc" => AmfFrameGenerator.TryCreate(g, model),
        _ => null,
    };

    public void Dispose()
    {
        Stop();
        _mailbox.Dispose();
        _started.Dispose();
    }
}

/// <summary>Downscales presented frames to 480p JPEG for the UI, at most 10 per second.</summary>
public sealed class PreviewTap : IDisposable
{
    private RenderTexture? _small;
    private Vortice.Direct3D11.ID3D11Texture2D? _staging;
    private Vortice.Direct3D11.ID3D11Buffer? _cb;
    private byte[] _pixels = [];
    private long _lastQpc;
    private bool _copyPending;
    private volatile bool _encoding;

    /// <summary>
    /// Every 100 ms: read back the copy made at the previous offer (the GPU is done with it by now),
    /// hand it to a JPEG worker, then downscale and copy the current frame for next time.
    /// </summary>
    public unsafe void Offer(GraphicsDevice g, RenderTexture frame, Action<byte[]> deliver, float intervalMs = 100)
    {
        if (intervalMs <= 0)
        {
            // Paused: the window keeps its last picture; a copy made before the pause would be stale later.
            _copyPending = false;
            return;
        }
        if (Qpc.ToMs(Qpc.Now - _lastQpc) < intervalMs) return;
        _lastQpc = Qpc.Now;
        if (_copyPending && !_encoding && _small != null)
        {
            int pw = _small.Width, ph = _small.Height;
            if (_pixels.Length != pw * ph * 4) _pixels = new byte[pw * ph * 4];
            var map = g.Context.Map(_staging!, 0, Vortice.Direct3D11.MapMode.Read, Vortice.Direct3D11.MapFlags.None);
            fixed (byte* dst = _pixels)
                for (int y = 0; y < ph; y++)
                    Buffer.MemoryCopy((byte*)map.DataPointer + y * map.RowPitch, dst + y * pw * 4, pw * 4, pw * 4);
            g.Context.Unmap(_staging!, 0);
            _copyPending = false;
            _encoding = true;
            var pixels = _pixels;
            Task.Run(() =>
            {
                try { deliver(Jpeg.Encode(pixels, pw, ph, 70)); }
                catch (Exception ex) { Log.Debug(ex, "Preview encode failed"); }
                finally { _encoding = false; }
            });
        }
        if (_copyPending || _encoding) return;

        int h = 480, w = Math.Max(2, (int)Math.Round(frame.Width * 480.0 / frame.Height)) & ~1;
        if (_small == null || _small.Width != w)
        {
            _small?.Dispose();
            _staging?.Dispose();
            _small = new RenderTexture(g.Device, w, h);
            _staging = g.Device.CreateTexture2D(new Vortice.Direct3D11.Texture2DDescription(Vortice.DXGI.Format.R8G8B8A8_UNorm, (uint)w, (uint)h, 1, 1, Vortice.Direct3D11.BindFlags.None, Vortice.Direct3D11.ResourceUsage.Staging, Vortice.Direct3D11.CpuAccessFlags.Read));
        }
        _cb ??= g.CreateConstantBuffer(System.Runtime.InteropServices.Marshal.SizeOf<ScaleConstants>());
        g.Update(_cb, new ScaleConstants
        {
            SrcSize = new Vector4(frame.Width, frame.Height, 1f / frame.Width, 1f / frame.Height),
            SrcRect = new Vector4(0, 0, 1, 1),
        });
        g.Draw(_small.Rtv, g.PixelShader("scale.hlsl", "PSBilinear"), _small.Viewport, [frame.Srv], _cb);
        g.Context.CopyResource(_staging!, _small.Texture);
        _copyPending = true;
    }

    public void Dispose()
    {
        _small?.Dispose();
        _staging?.Dispose();
        _cb?.Dispose();
    }
}

public static class Jpeg
{
    private static readonly System.Drawing.Imaging.ImageCodecInfo? Codec =
        System.Drawing.Imaging.ImageCodecInfo.GetImageEncoders().FirstOrDefault(c => c.MimeType == "image/jpeg");

    public static unsafe byte[] Encode(byte[] rgba, int w, int h, long quality)
    {
        using var bmp = new System.Drawing.Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
        var bd = bmp.LockBits(new System.Drawing.Rectangle(0, 0, w, h), System.Drawing.Imaging.ImageLockMode.WriteOnly, bmp.PixelFormat);
        fixed (byte* src = rgba)
        {
            for (int y = 0; y < h; y++)
            {
                byte* s = src + y * w * 4;
                byte* d = (byte*)bd.Scan0 + y * bd.Stride;
                for (int x = 0; x < w; x++)
                {
                    d[0] = s[2]; d[1] = s[1]; d[2] = s[0];
                    s += 4; d += 3;
                }
            }
        }
        bmp.UnlockBits(bd);
        using var ms = new MemoryStream();
        var p = new System.Drawing.Imaging.EncoderParameters(1);
        p.Param[0] = new System.Drawing.Imaging.EncoderParameter(System.Drawing.Imaging.Encoder.Quality, quality);
        bmp.Save(ms, Codec!, p);
        return ms.ToArray();
    }
}

