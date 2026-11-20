using System.Diagnostics;
using System.Text.Json;
using NAudio.CoreAudioApi;
using Serilog;
using TretioDLSS.Audio;
using TretioDLSS.Capture;
using TretioDLSS.Core;
using TretioDLSS.Diagnostics;
using TretioDLSS.Engines;
using TretioDLSS.Nvidia;
using TretioDLSS.Output;
using TretioDLSS.Pipeline;
using TretioDLSS.Platform;

namespace TretioDLSS.App;

/// <summary>Owns settings, devices, the pipeline and the UI messages for the windowed app.</summary>
public sealed class AppHost : IDisposable
{
    public SettingsService Settings { get; }
    public UpdateService Updates { get; } = new();
    public MessageService Messages { get; }
    public Capabilities Caps { get; private set; } = new();

    private List<CaptureDevice> _devices = new();
    private List<WindowEntry> _windows = new();
    private List<MonitorInfo> _monitors = new();
    private List<AudioDevice> _audioIn = new(), _audioOut = new();
    private PipelineHost? _pipeline;
    private readonly AudioBridge _audio = new();
    private readonly object _pipelineGate = new();
    private Timer? _statsTimer;
    private long _lastSource, _lastReal, _lastPresented;
    private long _lastStatsTick;
    private readonly Queue<double> _histReal = new(), _histGen = new();
    private readonly Dictionary<string, Hint> _hints = new();
    private readonly Queue<(long Tick, long Src, long Real, long Presented)> _rateWindow = new();
    private (CadenceState, string) _lastCadence;
    private volatile bool _previewActive = true;
    private HotkeyService? _hotkeys;
    private PipelineStatus _lastStatus = PipelineStatus.Idle;
    private string? _lastPipelineMessage;
    private int _blackFrames;

    public sealed record HintAction(string Label, string Method, object? Parameters = null);
    public sealed record Hint(string Id, string Level, string Text, HintAction? Action = null);

    public AppHost(SettingsService settings, MessageService messages)
    {
        if (SafeMode.Disabled is string disabled)
            _hints["safemode"] = new Hint("safemode", "warning", SafeMode.Message(disabled));
        Settings = settings;
        Messages = messages;
        Messages.Received += (m, p) => _ = Task.Run(() => Handle(m, p));
        Messages.Connected += () => { };
        Settings.Changed += OnSettingsChanged;
        _discord.StatusChanged += PushState;
    }

    public void Start()
    {
        VfxRuntime.ConfiguredPath = Settings.Current.Advanced.VfxRuntimePath;
        RefreshDevices(quiet: true);
        Task.Run(async () =>
        {
            Caps = Capabilities.Detect();
            ApplyFirstRunDefaults();
            PushState();
            await AutoDownload();
        });
        Task.Run(async () =>
        {
            await Updates.CheckAsync();
            PushState();
        });
        _statsTimer = new Timer(_ => PushStats(), null, 250, 250);
        try
        {
            _hotkeys = new HotkeyService();
            RegisterHotkeys();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Hotkeys unavailable");
        }
    }

    /// <summary>Fetches NVIDIA's runtime for the engines this GPU can run, so nobody has to press Download.</summary>
    private async Task AutoDownload()
    {
        foreach (var id in new[] { "vfx-vsr", "vfx-vfg" })
        {
            bool fits = id == "vfx-vfg" ? Caps.Ada : Caps.Nvidia;
            if (!fits || VfxRuntime.HasFeature(id)) continue;
            Log.Information("Downloading {Id} automatically", id);
            string? error = await VfxInstaller.InstallAsync(id, PushState);
            if (error != null)
            {
                Log.Warning("Automatic download of {Id} failed: {Error}", id, error);
                continue;
            }
            Caps = Capabilities.Detect();
            PushState();
        }
    }

    private void ApplyFirstRunDefaults()
    {
        var s = Settings.Current;
        bool changed = false;
        if (s.Source.Kind == "uvc" && string.IsNullOrEmpty(s.Source.DeviceId) && _devices.Count > 0)
        {
            var dev = CaptureCatalog.PickDefault(_devices) ?? _devices[0];
            ApplyDevice(s, dev);
            changed = true;
        }
        if (string.IsNullOrEmpty(s.Display.MonitorId) && _monitors.Count > 0)
        {
            s.Display.MonitorId = (_monitors.FirstOrDefault(m => m.Primary) ?? _monitors[0]).Id;
            changed = true;
        }
        // Smooth Motion was removed and Blend is only a test stand-in now: both move to a real engine.
        string realEngine = Caps.Ada ? "vfx-vfg" : Caps.AmdPipeline && Caps.AmfVersion != null ? "amd-frc" : RifeFrameGenerator.Models().Count > 0 ? "rife" : "off";
        foreach (var fg in s.Profiles.Select(x => x.FrameGen).Append(s.FrameGen).Where(f => f.Engine is "smooth-motion" or "blend"))
        {
            fg.Engine = realEngine;
            fg.Model = realEngine == "rife" ? "rife426" : realEngine == "amd-frc" ? "native" : "medium";
            fg.Multiplier = realEngine == "amd-frc" ? 2 : Math.Max(2, fg.Multiplier);
            changed = true;
        }
        // NVIDIA Frame Generation is the default, but needs an RTX 40 or 50; other GPUs get their best engine.
        if (s.FrameGen.Engine == "vfx-vfg" && !Caps.Ada && realEngine != "vfx-vfg")
        {
            s.FrameGen.Engine = realEngine;
            s.FrameGen.Model = realEngine == "rife" ? "rife426" : "native";
            if (realEngine == "amd-frc") s.FrameGen.Multiplier = 2;
            changed = true;
        }
        // Settings the UI no longer shows go back to automatic, so an old value cannot act unseen.
        if (s.Source.Api != "auto" || s.Source.File.Container != 60 || s.Source.Pattern.Cadence != "30in60" || s.Source.Pattern.Layout != "nv12"
            || s.Cadence.Mode != "auto" || s.FrameGen.StageOrder != "auto" || s.Display.Pacing != "auto" || s.Advanced.VfxRuntimePath != "")
        {
            s.Source.Api = "auto";
            s.Source.File.Container = 60;
            s.Source.Pattern.Cadence = "30in60";
            s.Source.Pattern.Layout = "nv12";
            s.Cadence.Mode = "auto";
            s.FrameGen.StageOrder = "auto";
            s.Display.Pacing = "auto";
            s.Advanced.VfxRuntimePath = "";
            VfxRuntime.ConfiguredPath = "";
            changed = true;
        }
        // The driver's RTX Video path was removed; the SDK one (vfx-vsr) is RTX Video now.
        foreach (var up in s.Profiles.Select(x => x.Upscale).Append(s.Upscale).Where(u => u.Engine == "rtx-video"))
        {
            up.Engine = "vfx-vsr";
            up.Options.TryAdd("quality", "high");
            changed = true;
        }
        if (s.Upscale.Engine == "vfx-vsr" && !Caps.Nvidia)
        {
            s.Upscale.Engine = "fsr1";
            changed = true;
        }
        if (changed) Settings.Save();
    }

    private static void ApplyDevice(Settings s, CaptureDevice dev)
    {
        s.Source.DeviceId = dev.Id;
        var fmt = FormatPicker.PickDefault(dev.Formats);
        if (fmt != null)
        {
            s.Source.Width = fmt.Width;
            s.Source.Height = fmt.Height;
            s.Source.Fps = fmt.Fps;
            s.Source.Subtype = fmt.Subtype;
        }
    }

    public void RefreshDevices(bool quiet = false)
    {
        try { _devices = CaptureCatalog.Enumerate(); } catch (Exception ex) { Log.Warning(ex, "Capture device list failed"); }
        try { _windows = WindowList.Enumerate(); } catch (Exception ex) { Log.Warning(ex, "Window list failed"); }
        try { _monitors = Monitors.Enumerate(); } catch (Exception ex) { Log.Warning(ex, "Monitor list failed"); }
        _audioIn = AudioDevices.List(DataFlow.Capture);
        _audioOut = AudioDevices.List(DataFlow.Render);
        if (!quiet) PushState();
    }

    // ---- messages from the UI ----

    private void Handle(string method, JsonElement p)
    {
        try
        {
            switch (method)
            {
                case "NewConnection":
                    PushState();
                    break;
                case "UpdateSettings":
                    Settings.ApplyPatch(p);
                    // Picking the engine the last run died in is the explicit retry.
                    if (SafeMode.Disabled is string off && (Settings.Current.Upscale.Engine == off || Settings.Current.FrameGen.Engine == off))
                    {
                        SafeMode.Retry(off);
                        _hints.Remove("safemode");
                    }
                    PushState();
                    break;
                case "StartPipeline":
                    StartPipeline();
                    break;
                case "SetPreviewActive":
                    _previewActive = p.TryGetProperty("active", out var act) && act.GetBoolean();
                    _pipeline?.SetPreviewFps(_previewActive ? 30 : 0);
                    break;
                case "CheckForUpdates":
                    Task.Run(async () =>
                    {
                        await Updates.CheckAsync();
                        PushState();
                    });
                    break;
                case "InstallUpdate":
                    StopPipeline();
                    Updates.InstallAndRestart();
                    break;
                case "SearchDiscordGames":
                {
                    string q = p.TryGetProperty("query", out var qv) ? qv.GetString() ?? "" : "";
                    Task.Run(async () =>
                    {
                        var found = await _discord.Search(q);
                        await Messages.Send("DiscordGames", new { query = q, games = found.Select(g => new { id = g.Id, name = g.Name, icon = g.Icon }) });
                    });
                    break;
                }
                case "StopPipeline":
                    StopPipeline();
                    break;
                case "RefreshDevices":
                    RefreshDevices();
                    Toast("info", "Devices refreshed");
                    break;
                case "ProbeSource":
                {
                    string id = p.GetProperty("deviceId").GetString() ?? "";
                    var dev = _devices.FirstOrDefault(d => d.Id == id);
                    if (dev != null)
                    {
                        Settings.Mutate(s => ApplyDevice(s, dev));
                        PushState();
                    }
                    break;
                }
                case "SelectProfile":
                    SelectProfile(p.GetProperty("name").GetString() ?? "");
                    break;
                case "SaveProfile":
                {
                    string name = p.GetProperty("name").GetString() ?? "";
                    if (string.IsNullOrWhiteSpace(name) || Profiles.BuiltIns.Any(b => b.Name == name)) break;
                    Settings.Mutate(s =>
                    {
                        s.Profiles.RemoveAll(x => x.Name == name);
                        s.Profiles.Add(Profiles.Capture(s, name));
                        s.ActiveProfile = name;
                    });
                    PushState();
                    break;
                }
                case "DeleteProfile":
                {
                    string name = p.GetProperty("name").GetString() ?? "";
                    Settings.Mutate(s => s.Profiles.RemoveAll(x => x.Name == name));
                    PushState();
                    break;
                }
                case "SetFrameGenEngine":
                    SetFrameGenEngine(p.GetProperty("engine").GetString() ?? "off",
                        p.TryGetProperty("multiplier", out var mult) && mult.ValueKind == JsonValueKind.Number ? mult.GetInt32() : null);
                    break;
                case "GetCapabilities":
                    Task.Run(() =>
                    {
                        Caps = Capabilities.Detect();
                        _ = Messages.Send("Capabilities", new { rows = Caps.Rows });
                        PushState();
                    });
                    break;
                case "InstallComponent":
                {
                    string id = p.GetProperty("id").GetString() ?? "";
                    if (!VfxInstaller.Knows(id)) break;
                    Task.Run(async () =>
                    {
                        string? error = await VfxInstaller.InstallAsync(id, PushState);
                        if (error == null)
                        {
                            Caps = Capabilities.Detect();
                            PushState();
                        }
                        Toast(error == null ? "info" : "error", error == null ? $"{EngineCatalog.Name(id)} downloaded" : error);
                    });
                    break;
                }
                case "RunLatencyTest":
                    Task.Run(RunLatencyTest);
                    break;
                case "OpenLogs":
                    Process.Start(new ProcessStartInfo("explorer.exe", Path.GetDirectoryName(AppInfo.LogFile)!) { UseShellExecute = true });
                    break;
                case "OpenExternal":
                {
                    string url = p.GetProperty("url").GetString() ?? "";
                    if (url.StartsWith("ms-settings:") || url.StartsWith("https://"))
                        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                    break;
                }
                case "PickPath":
                    PickPath(p.GetProperty("kind").GetString() ?? "file");
                    break;
                case "SunshineRegister":
                    Task.Run(RegisterWithSunshine);
                    break;
                default:
                    Log.Debug("Unhandled message {Method}", method);
                    break;
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Handling {Method} failed", method);
            Toast("error", ex.Message);
        }
    }

    private void OnSettingsChanged(Settings s)
    {
        VfxRuntime.ConfiguredPath = s.Advanced.VfxRuntimePath;
        var p = _pipeline;
        if (p != null)
        {
            p.SetSharpness(s.Upscale.Sharpness);
            p.SetHdrLook(HdrLook(s));
            p.SetOverlay(s.Display.Overlay);
            p.SetWindowTitle(string.IsNullOrWhiteSpace(s.Display.WindowTitle) ? "TretioDLSS output" : s.Display.WindowTitle.Trim());
        }
        _audio.SetVolume(s.Audio.Volume);
        RegisterHotkeys();
        _discord.Update(s.Discord, _pipeline != null);
    }

    private void SelectProfile(string name)
    {
        var profile = Profiles.Find(Settings.Current, name);
        if (profile == null) return;
        Settings.Mutate(s => Profiles.Apply(s, profile));
        PushState();
        if (_pipeline != null) RestartPipeline();
    }

    private void SetFrameGenEngine(string engine, int? multiplier = null)
    {
        if (SafeMode.Disabled == engine)
        {
            SafeMode.Retry(engine);
            _hints.Remove("safemode");
        }
        Settings.Mutate(s =>
        {
            s.FrameGen.Engine = engine;
            // The model setting is shared; keep it meaningful for the engine just picked.
            if (engine == "rife" && !s.FrameGen.Model.StartsWith("rife")) s.FrameGen.Model = "rife426";
            else if (engine == "vfx-vfg" && s.FrameGen.Model is not ("low" or "medium" or "high")) s.FrameGen.Model = "medium";
            else if (engine == "amd-frc")
            {
                if (s.FrameGen.Model is not ("native" or "performance")) s.FrameGen.Model = "native";
                s.FrameGen.Multiplier = 2;
            }
            if (multiplier is int m) s.FrameGen.Multiplier = FrameGen.ClampMultiplier(engine, m);
            else if (engine != "off") s.FrameGen.Multiplier = FrameGen.ClampMultiplier(engine, s.FrameGen.Multiplier);
        });
        PushState();
        if (_pipeline != null) RestartPipeline();
    }

    // ---- pipeline ----

    public void StartPipeline()
    {
        lock (_pipelineGate)
        {
            if (_pipeline != null) return;
            var s = Settings.Current;
            PipelineConfig cfg;
            try
            {
                cfg = BuildConfig(s);
            }
            catch (Exception ex)
            {
                Toast("error", ex.Message);
                PushState();
                return;
            }
            var host = new PipelineHost(cfg);
            host.StatusChanged += () =>
            {
                // Esc or the output window's close button ended it: stop like the Stop button does.
                if (host.Status == PipelineStatus.Idle && _pipeline == host) Task.Run(StopPipeline);
                PushState();
            };
            host.Preview += jpeg => _ = Messages.Send("Preview", Convert.ToBase64String(jpeg));
            host.SetPreviewFps(_previewActive ? 30 : 0);
            _pipeline = host;
            _lastSource = _lastReal = _lastPresented = 0;
            _rateWindow.Clear();
            _histReal.Clear();
            _histGen.Clear();
            _blackFrames = 0;
            _hints.Remove("smoothmotion");
            PushState();
            try
            {
                host.Start();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Pipeline did not start");
                _pipeline = null;
                host.Dispose();
                _lastPipelineMessage = ex.Message;
                Toast("error", "Could not start: " + ex.Message);
                PushState();
                return;
            }
            StartAudio(s);
            Task.Delay(2500).ContinueWith(_ =>
            {
                if (_pipeline != null && SmoothMotion.PresentLayerLoaded())
                {
                    Log.Warning("The driver's Smooth Motion is active in this process");
                    _hints["smoothmotion"] = new Hint("smoothmotion", "warning", "NVIDIA Smooth Motion is turned on for TretioDLSS.exe in the NVIDIA app, so the driver adds frames on top of the app's. Turn it off there.");
                    PushState();
                }
            });
        }
        PushState();
    }

    private void StartAudio(Settings s)
    {
        if (s.Audio.InputId == "none" || s.Source.Kind is "pattern" or "raw" or "file" or "window") return;
        string? inputId = s.Audio.InputId;
        if (inputId == "auto")
        {
            var dev = _devices.FirstOrDefault(d => d.Id == s.Source.DeviceId);
            inputId = dev == null ? null : AudioDevices.MatchCaptureCard(_audioIn, dev.Name)?.Id;
        }
        if (inputId == null)
        {
            _hints["audio"] = new Hint("audio", "info", "No sound input matched the capture card. Pick it under Settings, Audio.");
            return;
        }
        _hints.Remove("audio");
        _audio.SetVolume(s.Audio.Volume);
        _audio.Start(inputId, s.Audio.OutputId);
        if (_audio.Error != null) _hints["audio"] = new Hint("audio", "warning", _audio.Error);
    }

    public bool PipelineRunning => _pipeline != null;

    private static HdrConfig HdrLook(Settings s)
    {
        var h = s.Display.Hdr;
        return new HdrConfig(h.PaperWhite, h.Expand, h.Brightness, h.Contrast, h.Saturation);
    }

    public void StopPipeline()
    {
        PipelineHost? p;
        lock (_pipelineGate)
        {
            p = _pipeline;
            _pipeline = null;
        }
        _audio.Stop();
        if (p != null)
        {
            _lastPipelineMessage = p.Status == PipelineStatus.Faulted ? p.StatusMessage : null;
            p.Dispose();
        }
        _hints.Remove("nosignal");
        _hints.Remove("smoothmotion");
        PushState();
    }

    private void RestartPipeline()
    {
        StopPipeline();
        StartPipeline();
    }

    public void TogglePipeline()
    {
        if (_pipeline != null) StopPipeline();
        else StartPipeline();
    }

    private PipelineConfig BuildConfig(Settings s)
    {
        var monitor = Monitors.Find(_monitors, s.Display.MonitorId) ?? throw new InvalidOperationException("No display found");
        var source = SourceFactory.FromSettings(s.Source, _devices);
        string fg = s.FrameGen.Engine;
        var sync = s.Display.Sync switch
        {
            "vsync" => SyncMode.Vsync,
            "tearing" => SyncMode.Tearing,
            "vrr" => SyncMode.Vrr,
            _ => monitor.Vrr && Caps.Tearing ? SyncMode.Vrr : SyncMode.Vsync,
        };
        var pacing = s.Display.Pacing switch
        {
            "clocked" => PacingMode.Clocked,
            "immediate" => PacingMode.Immediate,
            _ => PacingMode.Immediate,
        };
        return new PipelineConfig
        {
            SourceFactory = source,
            Upscaler = s.Upscale.Engine,
            UpscaleQuality = s.Upscale.Options.GetValueOrDefault("quality", "high"),
            FrameGen = fg,
            Multiplier = s.FrameGen.Multiplier,
            FrameGenModel = s.FrameGen.Model,
            Sharpness = s.Upscale.Sharpness,
            SizeMode = s.Upscale.Size,
            Color = new ColorSpec(s.Source.ColorMatrix, s.Source.ColorRange),
            CadenceMode = s.Cadence.Mode,
            FixedFps = s.Cadence.FixedFps,
            Pacing = pacing,
            Sync = sync,
            Overlay = s.Display.Overlay,
            Adapter = s.Advanced.Adapter,
            Monitor = monitor,
            Borderless = s.Display.Mode != "windowed",
            NoActivate = s.Source.Kind == "window",
            WindowTitle = string.IsNullOrWhiteSpace(s.Display.WindowTitle) ? "TretioDLSS output" : s.Display.WindowTitle.Trim(),
            TrimBlackBars = s.Source.Kind == "window",
            Hdr = s.Display.Hdr.Mode == "off" ? null : HdrLook(s),
            LossySource = s.Source.Kind == "window" || s.Source.Subtype == "MJPG",
        };
    }

    // ---- stats ----

    private void PushStats()
    {
        var p = _pipeline;
        if (p == null || p.Status != PipelineStatus.Running && p.Status != PipelineStatus.Waiting)
        {
            CheckPipelineEnded(p);
            return;
        }
        long now = Environment.TickCount64;
        double dt = _lastStatsTick == 0 ? 0.25 : Math.Max(0.05, (now - _lastStatsTick) / 1000.0);
        _lastStatsTick = now;
        var c = p.Counters;
        long src = Interlocked.Read(ref c.SourceFrames), real = Interlocked.Read(ref c.RealFrames), gen = Interlocked.Read(ref c.Generated);
        long presented = real + gen;
        // Rates over a 2 s window: one 250 ms tick would read 30 fps as 28 or 32.
        _rateWindow.Enqueue((now, src, real, presented));
        while (_rateWindow.Count > 9) _rateWindow.Dequeue();
        var first = _rateWindow.Peek();
        double span = Math.Max(0.05, (now - first.Tick) / 1000.0);
        double sourceFps = 0, realFps = 0, outFps = 0;
        if (_rateWindow.Count > 1)
        {
            sourceFps = (src - first.Src) / span;
            realFps = (real - first.Real) / span;
            outFps = (presented - first.Presented) / span;
        }
        if (p.CadenceState == CadenceState.Locked && p.ClockPeriodMs > 0)
        {
            double clockFps = 1000.0 / p.ClockPeriodMs;
            if (Math.Abs(clockFps - realFps) < realFps * 0.1)
            {
                if (realFps > 0) outFps *= clockFps / realFps;
                realFps = clockFps;
            }
        }
        _lastSource = src;
        _lastReal = real;
        _lastPresented = presented;
        p.LastRates = (sourceFps, realFps, outFps);
        var s = Settings.Current;
        double genFps = Math.Max(0, outFps - realFps);
        Smooth(_histReal, realFps);
        Smooth(_histGen, genFps);

        // Audio follows the picture.
        if (_audio.Running && s.Audio.AutoSync)
        {
            double videoDelay = p.MedianDelayMs();
            double extra = Math.Max(0, videoDelay - _audio.PathLatencyMs) + s.Audio.OffsetMs;
            _audio.SetExtraDelay(extra);
        }
        else if (_audio.Running)
        {
            _audio.SetExtraDelay(Math.Max(0, s.Audio.OffsetMs));
        }

        var info = p.SourceInfo;
        string cadence = p.CadenceLabel;
        var cadenceNow = (p.CadenceState, cadence);
        if (cadenceNow != _lastCadence)
        {
            Log.Information("Cadence {State} {Label}: hitches {Hitches}, relocks {Relocks}, source {Fps:0.0} fps", p.CadenceState, cadence, p.Hitches, p.Relocks, sourceFps);
            _lastCadence = cadenceNow;
        }
        string sourceDetail = info == null ? "" : cadence.Length > 0 && cadence != "1:1"
            ? $"{Math.Round(realFps)} real / {Math.Round(info.ContainerHz)} Hz"
            : $"{info.Width} x {info.Height}, {Math.Round(sourceFps)} fps";
        if (p.CadenceState == CadenceState.Unlocked && info != null) sourceDetail = $"Cadence unlocked, {Math.Round(info.ContainerHz)} Hz";
        var (cw, ch) = p.ContentSize;
        double upMs = p.Stages.RecentMean(1);
        string upDetail = info == null ? "" : $"{info.Height}p to {ch}p, {upMs:0.0} ms";
        if (p.CompareMode) upDetail = "Comparing with bilinear";
        else if (SafeMode.IsDisabled(s.Upscale.Engine)) upDetail = SafeMode.ShortMessage;
        else if (!string.IsNullOrEmpty(p.UpscalerStatus) && s.Upscale.Engine == "vfx-vsr") upDetail = p.UpscalerStatus;
        string fgDetail = s.FrameGen.Engine == "off" ? "Real frames only"
            : p.CadenceState == CadenceState.Unlocked ? "Paused until the cadence locks"
            : $"x{s.FrameGen.Multiplier}, {Math.Round(outFps)} fps out";
        var mon = Monitors.Find(_monitors, s.Display.MonitorId);
        string syncLabel = s.Display.Sync switch { "vsync" => "V-Sync", "vrr" => "VRR", "tearing" => "Tearing", _ => mon?.Vrr == true ? "VRR" : "V-Sync" };
        string outDetail = mon == null ? "" : $"{mon.Refresh:0} Hz, {syncLabel}";

        // No-signal hint: the card keeps sending black frames, or nothing at all.
        if (info?.Kind is "uvc" or "pattern") UpdateNoSignal(p, sourceFps);

        _ = Messages.Send("Stats", new
        {
            sourceFps,
            realFps,
            outputFps = outFps,
            outputEstimated = false,
            addedDelayMs = p.MedianDelayMs(),
            delayNotMeasured = false,
            gpuPercent = Math.Min(100, (p.Stages.RecentMean(0) + p.Stages.RecentMean(1) + p.Stages.RecentMean(3)) * realFps / 10 + p.Stages.RecentMean(2) * genFps / 10),
            dropped = Interlocked.Read(ref c.Dropped),
            late = Interlocked.Read(ref c.Late),
            cadence,
            containerHz = info?.ContainerHz ?? 0,
            sourceDetail,
            upscaleDetail = upDetail,
            frameGenDetail = fgDetail,
            outputDetail = outDetail,
            history = new { real = _histReal.ToArray(), generated = _histGen.ToArray() },
        });
    }

    private void UpdateNoSignal(PipelineHost p, double sourceFps)
    {
        // Black from the start for 2 s, or no frames at all for 2 s: the cable and HDCP hint.
        if (sourceFps < 0.5) _blackFrames++;
        else _blackFrames = 0;
        bool show = p.NoSignal || _blackFrames > 8;
        bool has = _hints.ContainsKey("nosignal");
        if (show && !has)
        {
            _hints["nosignal"] = new Hint("nosignal", "warning", "No picture from the capture card. Check the HDMI cable. On a PS5, turn off HDCP under Settings, System, HDMI.");
            PushState();
        }
        else if (!show && has)
        {
            _hints.Remove("nosignal");
            PushState();
        }
    }

    private static void Smooth(Queue<double> q, double v)
    {
        q.Enqueue(Math.Round(v, 1));
        while (q.Count > 40) q.Dequeue();
    }

    private void CheckPipelineEnded(PipelineHost? p)
    {
        if (p != null && p.Status == PipelineStatus.Faulted && _lastStatus != PipelineStatus.Faulted)
        {
            _lastStatus = PipelineStatus.Faulted;
            Toast("error", p.StatusMessage ?? "The pipeline stopped");
            Task.Run(StopPipeline);
        }
        if (p != null) _lastStatus = p.Status;
    }

    // ---- state to the UI ----

    private readonly DiscordPresence _discord = new();

    public void PushState()
    {
        _discord.Update(Settings.Current.Discord, _pipeline != null);
        _ = Messages.Send("State", BuildState());
    }

    private object BuildState()
    {
        var s = Settings.Current;
        var p = _pipeline;
        string status = p == null ? "idle" : p.Status switch
        {
            PipelineStatus.Starting => "starting",
            PipelineStatus.Running => "running",
            PipelineStatus.Stopping => "stopping",
            PipelineStatus.Waiting => "waiting",
            PipelineStatus.Faulted => "faulted",
            _ => "idle",
        };
        var hints = _hints.Values.ToList();
        var privacy = Privacy.Check();
        if (s.Source.Kind == "uvc" && !privacy.CameraAllowed)
            hints.Insert(0, new Hint("camera", "error", "Windows blocks camera access for desktop apps, which also blocks capture cards. Turn it on in Settings, Privacy and security, Camera."));
        if (p == null && _lastPipelineMessage != null)
            hints.Insert(0, new Hint("lasterror", "error", _lastPipelineMessage));
        if (s.FrameGen.Engine == "vfx-vfg" && Caps.Ada && !VfxRuntime.HasFeature("vfx-vfg") && VfxInstaller.State("vfx-vfg").State != "downloading")
            hints.Add(new Hint("vfg-download", "info", "NVIDIA Frame Generation is picked but not downloaded yet, so the picture runs without it.",
                new HintAction($"Download, {VfxInstaller.MissingBytes("vfx-vfg") / 1_000_000} MB", "InstallComponent", new { id = "vfx-vfg" })));
        return new
        {
            version = AppInfo.Version,
            settings = s,
            profiles = Profiles.List(s).Select(x => new { name = x.Name, builtIn = x.BuiltIn, summary = x.Summary }),
            devices = _devices.Select(d => new
            {
                id = d.Id,
                name = d.Name,
                formats = d.Formats.Select(f => new { width = f.Width, height = f.Height, fps = f.Fps, subtype = f.Subtype }),
            }),
            windows = _windows.Select(w => new { hwnd = w.Hwnd.ToString(), title = w.Title, process = w.Process }),
            monitors = _monitors.Select(m => new { id = m.Id, name = m.Name, width = m.Width, height = m.Height, refresh = m.Refresh, vrr = m.Vrr, hdr = m.Hdr, primary = m.Primary, tv = m.Tv, adapter = m.Adapter }),
            audioInputs = _audioIn.Select(a => new { id = a.Id, name = a.Name, isDefault = a.IsDefault }),
            audioOutputs = _audioOut.Select(a => new { id = a.Id, name = a.Name, isDefault = a.IsDefault }),
            upscalers = EngineCatalog.Upscalers(Caps),
            frameGenerators = EngineCatalog.FrameGenerators(Caps),
            pipeline = new { status, message = p?.StatusMessage },
            gpuName = Caps.GpuName,
            update = new { status = Updates.Status, ready = Updates.PendingVersion },
            discordStatus = _discord.Status,
            hints = hints.Select(h => new { id = h.Id, level = h.Level, text = h.Text, action = h.Action == null ? null : new { label = h.Action.Label, method = h.Action.Method, parameters = h.Action.Parameters } }),
        };
    }

    public void Toast(string level, string text) => _ = Messages.Send("Toast", new { level, text });

    // ---- odds and ends ----

    private void PickPath(string kind)
    {
        var t = new Thread(() =>
        {
            string? path = null;
            if (kind == "raw")
            {
                using var dlg = new System.Windows.Forms.FolderBrowserDialog { Description = "Pick a raw recording folder" };
                if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK) path = dlg.SelectedPath;
            }
            else
            {
                using var dlg = new System.Windows.Forms.OpenFileDialog { Filter = "Video files|*.mp4;*.mkv;*.mov;*.avi;*.wmv;*.webm|All files|*.*" };
                if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK) path = dlg.FileName;
            }
            if (path == null) return;
            Settings.Mutate(s =>
            {
                if (kind == "raw") s.Source.Raw.Path = path;
                else s.Source.File.Path = path;
            });
            PushState();
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
    }

    private void RunLatencyTest()
    {
        if (_pipeline != null)
        {
            _ = Messages.Send("LatencyResult", new { samples = 0, minMs = 0, medianMs = 0, maxMs = 0, error = "Stop the pipeline first" });
            return;
        }
        var result = LatencyTest.Run(Settings.Current, _devices, _monitors);
        _ = Messages.Send("LatencyResult", new { samples = result.Samples, minMs = result.MinMs, medianMs = result.MedianMs, maxMs = result.MaxMs, error = result.Error });
    }

    private void RegisterWithSunshine()
    {
        var r = Sunshine.Register(AppInfo.Name, Settings.Current.ActiveProfile);
        Toast(r.Ok ? "success" : "warning", r.Message);
    }

    // ---- hotkeys ----

    private string _hotkeyKey = "";

    private void RegisterHotkeys()
    {
        if (_hotkeys == null) return;
        var h = Settings.Current.Hotkeys;
        string key = $"{h.ToggleRun}|{h.ToggleFrameGen}|{h.ToggleOverlay}|{h.CompareUpscaler}|{h.ExitFullscreen}";
        if (key == _hotkeyKey) return;
        _hotkeyKey = key;
        _hotkeys.Clear();
        _hotkeys.Register(h.ToggleRun, () => Task.Run(TogglePipeline));
        _hotkeys.Register(h.ToggleFrameGen, () => Task.Run(() =>
        {
            var s = Settings.Current;
            string next = s.FrameGen.Engine == "off" ? (_lastFrameGen ?? "vfx-vfg") : "off";
            if (s.FrameGen.Engine != "off") _lastFrameGen = s.FrameGen.Engine;
            SetFrameGenEngine(next);
        }));
        _hotkeys.Register(h.ToggleOverlay, () =>
        {
            Settings.Mutate(s => s.Display.Overlay = !s.Display.Overlay);
            PushState();
        });
        _hotkeys.Register(h.CompareUpscaler, () => _pipeline?.ToggleCompare());
        _hotkeys.Register(h.ExitFullscreen, () => Task.Run(StopPipeline));
    }

    private string? _lastFrameGen;

    public void Dispose()
    {
        _statsTimer?.Dispose();
        _hotkeys?.Dispose();
        StopPipeline();
        _audio.Dispose();
        _discord.Dispose();
        Updates.ApplyOnExit();
    }
}
