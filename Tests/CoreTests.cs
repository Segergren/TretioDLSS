using System.Text.Json;
using System.Text.Json.Nodes;
using TretioDLSS.App;
using TretioDLSS.Audio;
using TretioDLSS.Capture;
using TretioDLSS.Core;
using TretioDLSS.Engines;
using TretioDLSS.Output;
using TretioDLSS.Pipeline;
using TretioDLSS.Platform;

namespace TretioDLSS.Tests;

public class SettingsTests
{
    private static string TempFile() => Path.Combine(Path.GetTempPath(), "tretio-tests", Guid.NewGuid() + ".json");

    [Fact]
    public void RoundTripKeepsValues()
    {
        var path = TempFile();
        var svc = new SettingsService(path);
        Assert.False(svc.Load());
        svc.Current.Upscale.Engine = "fsr1";
        svc.Current.Audio.OffsetMs = 40;
        svc.Current.Source.Window.Title = "PS Remote Play";
        svc.Save();
        var again = new SettingsService(path);
        Assert.True(again.Load());
        Assert.Equal("fsr1", again.Current.Upscale.Engine);
        Assert.Equal(40, again.Current.Audio.OffsetMs);
        Assert.Equal("PS Remote Play", again.Current.Source.Window.Title);
        Assert.Contains("\"upscale\"", File.ReadAllText(path));
    }

    [Fact]
    public void MigratesFilesWithoutSchema()
    {
        var node = new JsonObject { ["upscale"] = new JsonObject { ["engine"] = "nis" } };
        SettingsService.Migrate(node);
        Assert.Equal(1, node["schemaVersion"]!.GetValue<int>());
        var s = node.Deserialize<Settings>(Json.Options)!;
        Assert.Equal("nis", s.Upscale.Engine);
        Assert.Equal("auto", s.Display.Sync);
    }

    [Fact]
    public void BrokenFileFallsBackToDefaults()
    {
        var path = TempFile();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{ not json");
        var svc = new SettingsService(path);
        Assert.True(svc.Load());
        Assert.Equal("vfx-vsr", svc.Current.Upscale.Engine);
        Assert.True(File.Exists(path + ".broken"));
    }

    [Fact]
    public void PatchMergesDeepAndClamps()
    {
        var svc = new SettingsService(TempFile());
        svc.Load();
        using var doc = JsonDocument.Parse("""{"upscale":{"sharpness":3},"display":{"sync":"vsync"},"audio":{"offsetMs":999}}""");
        svc.ApplyPatch(doc.RootElement);
        Assert.Equal(1, svc.Current.Upscale.Sharpness);
        Assert.Equal("vfx-vsr", svc.Current.Upscale.Engine);
        Assert.Equal("vsync", svc.Current.Display.Sync);
        Assert.Equal("borderless", svc.Current.Display.Mode);
        Assert.Equal(300, svc.Current.Audio.OffsetMs);
    }

    [Fact]
    public void ChangesAreStoredIntoTheActiveProfile()
    {
        var svc = new SettingsService(TempFile());
        svc.Load();
        Assert.Equal(Profiles.DefaultName, Assert.Single(svc.Current.Profiles).Name);
        svc.Mutate(s =>
        {
            s.Source.Width = 3840;
            s.Display.MonitorId = "tv";
            s.FrameGen.Engine = "dlss-fg";
        });
        var p = Profiles.Find(svc.Current, Profiles.DefaultName)!;
        Assert.Equal(3840, p.Source.Width);
        Assert.Equal("tv", p.Display.MonitorId);
        Assert.Equal("dlss-fg", p.FrameGen.Engine);
    }

    [Fact]
    public void SwitchingProfilesRestoresTheWholeSetupButNotAudio()
    {
        var svc = new SettingsService(TempFile());
        svc.Load();
        svc.Mutate(s => { s.Source.Height = 1440; s.Display.MonitorId = "mon"; s.Audio.InputId = "mic"; });
        svc.Mutate(s =>
        {
            string name = Profiles.NextName(s);
            s.Profiles.Add(Profiles.Capture(s, name));
            s.ActiveProfile = name;
        });
        Assert.Equal("Profile 2", svc.Current.ActiveProfile);
        svc.Mutate(s => { s.Source.Height = 2160; s.Display.MonitorId = "tv"; s.Audio.InputId = "other"; });

        svc.Mutate(s => Profiles.Apply(s, Profiles.Find(s, Profiles.DefaultName)!));
        Assert.Equal(1440, svc.Current.Source.Height);
        Assert.Equal("mon", svc.Current.Display.MonitorId);
        Assert.Equal("other", svc.Current.Audio.InputId);
        Assert.Equal(2160, Profiles.Find(svc.Current, "Profile 2")!.Source.Height);
        Assert.Contains("2160p", Profiles.Summary(Profiles.Find(svc.Current, "Profile 2")!));
    }

    [Fact]
    public void ActiveProfileSurvivesARestart()
    {
        string file = TempFile();
        var svc = new SettingsService(file);
        svc.Load();
        svc.Mutate(s =>
        {
            s.Profiles.Add(Profiles.Capture(s, "TV 4K"));
            s.ActiveProfile = "TV 4K";
            s.Source.Width = 3840;
        });
        svc.Save();
        var again = new SettingsService(file);
        again.Load();
        Assert.Equal("TV 4K", again.Current.ActiveProfile);
        Assert.Equal(3840, again.Current.Source.Width);
    }
}

public class FormatPickerTests
{
    // The GM-29F's published mode list.
    private static readonly List<CaptureFormat> Gm29f =
    [
        new(3840, 2160, 60, "MJPG"), new(4096, 2160, 60, "MJPG"), new(2560, 1440, 120, "MJPG"), new(1920, 1080, 120, "MJPG"),
        new(2560, 1440, 50, "YUY2"), new(2560, 1440, 50, "NV12"), new(1920, 1080, 120, "NV12"),
        new(1920, 1080, 59.94, "YUY2"), new(1920, 1080, 59.94, "NV12"), new(1280, 720, 60, "NV12"), new(3840, 2160, 25, "YUY2"),
    ];

    [Fact]
    public void PicksUncompressed1080p60Nv12()
    {
        var f = FormatPicker.PickDefault(Gm29f)!;
        Assert.Equal((1920, 1080, "NV12"), (f.Width, f.Height, f.Subtype));
        Assert.True(FormatPicker.RateMatches(f.Fps, 60));
    }

    [Fact]
    public void NeverPicksMjpeg()
    {
        var onlyMjpgAndSmall = Gm29f.Where(f => f.Subtype == "MJPG" || f.Width == 1280).ToList();
        Assert.Equal("NV12", FormatPicker.PickDefault(onlyMjpgAndSmall)!.Subtype);
        Assert.Null(FormatPicker.PickDefault(Gm29f.Where(f => f.Subtype == "MJPG")));
    }

    [Fact]
    public void FallsBackToYuy2()
    {
        var f = FormatPicker.PickDefault(Gm29f.Where(f => f.Subtype != "NV12"))!;
        Assert.Equal("YUY2", f.Subtype);
        Assert.Equal(1920, f.Width);
    }

    [Theory]
    [InlineData(59.94, 60, true)]
    [InlineData(60, 59.94, true)]
    [InlineData(50, 60, false)]
    [InlineData(119.88, 120, true)]
    public void RateTolerance(double a, double b, bool match) => Assert.Equal(match, FormatPicker.RateMatches(a, b));
}

public class MiscTests
{
    [Fact]
    public void PrivacyEvaluation()
    {
        Assert.True(Privacy.Evaluate(("Allow", null, null), ("Allow", "Allow", null)).CameraAllowed);
        Assert.False(Privacy.Evaluate(("Allow", "Deny", null), (null, null, null)).CameraAllowed);
        Assert.False(Privacy.Evaluate(("Deny", null, null), (null, null, null)).CameraAllowed);
        Assert.False(Privacy.Evaluate((null, null, "Deny"), (null, null, null)).CameraAllowed);
        Assert.False(Privacy.Evaluate((null, null, null), ("Allow", "Deny", null)).MicrophoneAllowed);
    }

    [Theory]
    [InlineData("Ctrl+Alt+F10", 0x3u, 0x79u)]
    [InlineData("Shift+F1", 0x4u, 0x70u)]
    [InlineData("Ctrl+K", 0x2u, (uint)'K')]
    public void HotkeysParse(string text, uint mods, uint vk)
    {
        Assert.True(HotkeyService.TryParse(text, out var m, out var v));
        Assert.Equal(mods, m);
        Assert.Equal(vk, v);
    }

    [Fact]
    public void BadHotkeyIsRejected() => Assert.False(HotkeyService.TryParse("Ctrl+Alt", out _, out _));

    [Fact]
    public void WebSocketNeedsTokenAndOrigin()
    {
        var m = new MessageService(0, "abc");
        m.AllowOrigin("http://localhost:44140");
        Assert.True(m.Accepts("http://localhost:44140", "abc"));
        Assert.True(m.Accepts(null, "abc"));
        Assert.False(m.Accepts("http://localhost:44140", "wrong"));
        Assert.False(m.Accepts("https://evil.example", "abc"));
        Assert.False(m.Accepts("http://localhost:44140", null));
    }

    [Theory]
    [InlineData(1920, 1080, 2560, 1440, 0, 0, 2560, 1440)]
    [InlineData(1920, 1080, 3440, 1440, 440, 0, 2560, 1440)]
    [InlineData(1920, 1080, 1920, 1200, 0, 60, 1920, 1080)]
    public void FitLetterboxes(int sw, int sh, int dw, int dh, int x, int y, int w, int h) =>
        Assert.Equal((x, y, w, h), OutputSink.Fit(sw, sh, dw, dh));

    [Fact]
    public void FitSourceModeNeverUpscales() => Assert.Equal((320, 180, 1920, 1080), OutputSink.Fit(1920, 1080, 2560, 1440, "source"));

    [Fact]
    public void FsrConstantsMatchReference()
    {
        var c = Fsr1Upscaler.EasuConstants(1920, 1080, 2560, 1440);
        Assert.Equal(0.75f, BitConverter.UInt32BitsToSingle(c.C0x), 5);
        Assert.Equal(0.5f * 0.75f - 0.5f, BitConverter.UInt32BitsToSingle(c.C0z), 5);
        Assert.Equal(4f / 1080f, BitConverter.UInt32BitsToSingle(c.C3y), 7);
        Assert.Null(Rcas.Stops(0));
        Assert.Equal(0f, Rcas.Stops(1));
    }

    [Fact]
    public void CardAudioMatchesByName()
    {
        var inputs = new List<AudioDevice>
        {
            new("1", "Microphone (Shure MV7)", true, null),
            new("2", "Digital Audio Interface (USB3 Video)", false, null),
        };
        Assert.Equal("2", AudioDevices.MatchCaptureCard(inputs, "USB3 Video")!.Id);
    }

    [Fact]
    public void TvNamesAreRecognized()
    {
        Assert.True(Monitors.LooksLikeTv("LG TV SSCR2"));
        Assert.True(Monitors.LooksLikeTv("SONY TV"));
        Assert.False(Monitors.LooksLikeTv("Odyssey G60SD"));
        Assert.False(Monitors.LooksLikeTv("LG ULTRAGEAR"));
    }
}

public class ResamplerTests
{
    [Fact]
    public void HoldsLevelWithDriftingClocks()
    {
        // Input clock 0.1% fast against the output: without correction the buffer would grow forever.
        var r = new AdaptiveResampler(2, 48000, 48000) { TargetFrames = 48000 * 0.030 };
        var inBlock = new float[480 * 2];
        var outBlock = new float[480 * 2];
        double inAcc = 0;
        var levels = new List<double>();
        for (int block = 0; block < 6000; block++) // 60 s of 10 ms blocks
        {
            inAcc += 480 * 1.001;
            int n = (int)inAcc;
            inAcc -= n;
            r.Write(new float[n * 2]);
            r.Read(outBlock);
            if (block > 3000) levels.Add(r.LevelFrames / 48.0);
        }
        Assert.True(levels.Min() > 10 && levels.Max() < 60, $"level {levels.Min():0.0} to {levels.Max():0.0} ms");
        Assert.InRange(r.Ratio, 1.0, 1.0 + AdaptiveResampler.MaxCorrection);
        Assert.Equal(0, r.Overruns);
    }

    [Fact]
    public void PrimesBeforePlaying()
    {
        var r = new AdaptiveResampler(2, 48000, 48000) { TargetFrames = 100 };
        var o = new float[200];
        Array.Fill(o, 1f);
        r.Read(o);
        Assert.All(o, v => Assert.Equal(0f, v));
        Assert.Equal(0, r.Underruns);
    }

    [Fact]
    public void UnderrunsAreCountedAndSilent()
    {
        var r = new AdaptiveResampler(2, 48000, 48000) { TargetFrames = 100 };
        var block = new float[120 * 2];
        Array.Fill(block, 0.5f);
        r.Write(block);
        var o = new float[200 * 2];
        r.Read(o);
        Assert.Equal(1, r.Underruns);
        Assert.Equal(0f, o[^1]);
        Assert.Equal(0.5f, o[0]);
    }
}

public class HotPlugTests
{
    private sealed class NullSource : IVideoSource
    {
        public SourceInfo Info { get; } = new("Stand-in", "uvc", 1920, 1080, 60, PixelLayout.Nv12, true);
        public void Start(IFrameSink sink) { }
        public void Stop() { }
        public void Dispose() { }
    }

    [Fact]
    public void RemovalWaitsAndReturnResumes()
    {
        using var host = new PipelineHost(new PipelineConfig { SourceFactory = () => new NullSource() });
        var seen = new List<PipelineStatus>();
        host.StatusChanged += () => seen.Add(host.Status);
        host.OnSourceEvent(SourceEvent.DeviceLost, "The capture card is not connected. Waiting for it to come back.");
        Assert.Equal(PipelineStatus.Waiting, host.Status);
        Assert.Contains("not connected", host.StatusMessage);
        host.OnSourceEvent(SourceEvent.DeviceBack);
        Assert.Equal(PipelineStatus.Running, host.Status);
        Assert.Null(host.StatusMessage);
        Assert.Equal([PipelineStatus.Waiting, PipelineStatus.Running], seen);
    }

    [Fact]
    public void ErrorsNeverFault()
    {
        using var host = new PipelineHost(new PipelineConfig { SourceFactory = () => new NullSource() });
        host.OnSourceEvent(SourceEvent.Error, "stream error");
        Assert.Equal(PipelineStatus.Waiting, host.Status);
        host.OnSourceEvent(SourceEvent.Started);
        Assert.Equal(PipelineStatus.Running, host.Status);
    }

    [Theory]
    [InlineData(unchecked((int)0x80070005), "camera access")]
    [InlineData(unchecked((int)0xC00D3704), "in use")]
    [InlineData(unchecked((int)0xC00D36B5), "not connected")]
    [InlineData(unchecked((int)0x80004005), "stopped delivering")]
    public void CaptureErrorsNameTheCause(int hr, string expected)
    {
        var ex = new System.Runtime.InteropServices.COMException("x", hr);
        Assert.Contains(expected, UvcSource.Describe(ex), StringComparison.OrdinalIgnoreCase);
    }
}

[Collection("SafeMode")]
public class SafeModeTests
{
    private static string Marker() => Path.Combine(Path.GetTempPath(), "tretio-tests", Guid.NewGuid() + ".marker");

    [Fact]
    public void MarkerFromCrashDisablesEngineOnce()
    {
        SafeMode.ResetForTests();
        var path = Marker();
        SafeMode.Enter(["vfx-vsr", "off"], path);
        Assert.StartsWith("vfx-vsr|", File.ReadAllText(path));
        // A marker naming a dead process is a crash.
        File.WriteAllText(path, "vfx-vsr|999999");
        SafeMode.CheckAtStartup(path);
        Assert.True(SafeMode.IsDisabled("vfx-vsr"));
        Assert.False(File.Exists(path));
        Assert.Contains("RTX Video is off for this session", SafeMode.Message("vfx-vsr"));
        Assert.Contains(EngineCatalog.Upscalers(new Capabilities { Nvidia = true }), e => e.Id == "vfx-vsr" && !e.Available);
        SafeMode.Retry("vfx-vsr");
        Assert.Null(SafeMode.Disabled);
    }

    [Fact]
    public void NvidiaFrameGenerationGoesUpToEightTimes()
    {
        var vfg = Assert.Single(EngineCatalog.FrameGenerators(new Capabilities { Nvidia = true }), e => e.Id == "vfx-vfg");
        Assert.Equal([2, 3, 4, 5, 6, 7, 8], vfg.Multipliers);
        Assert.Equal(8, FrameGen.ClampMultiplier("vfx-vfg", 12));
        Assert.Equal(4, FrameGen.ClampMultiplier("dlss-fg", 8));
        Assert.Equal(2, FrameGen.ClampMultiplier("amd-frc", 4));
    }

    [Fact]
    public void DlssFrameGenerationIsListedUpToFourTimes()
    {
        var dlss = Assert.Single(EngineCatalog.FrameGenerators(new Capabilities { Nvidia = true }), e => e.Id == "dlss-fg");
        Assert.False(dlss.Available);
        Assert.Equal([2, 3, 4], dlss.Multipliers);
        Assert.Contains("DLSS FG x4", Profiles.Summary(new ProfileData { FrameGen = new FrameGenSettings { Engine = "dlss-fg", Multiplier = 4 } }));
    }

    [Fact]
    public void CleanStopLeavesNoMarker()
    {
        SafeMode.ResetForTests();
        var path = Marker();
        SafeMode.Enter(["rife"], path);
        SafeMode.Clear(path);
        SafeMode.CheckAtStartup(path);
        Assert.Null(SafeMode.Disabled);
    }

    [Fact]
    public void LiveProcessMarkerIsLeftAlone()
    {
        SafeMode.ResetForTests();
        var path = Marker();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var other = System.Diagnostics.Process.GetProcessesByName("explorer").FirstOrDefault() ?? System.Diagnostics.Process.GetCurrentProcess();
        File.WriteAllText(path, $"vfx-vsr|{other.Id}");
        SafeMode.CheckAtStartup(path);
        Assert.Null(SafeMode.Disabled);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void ShaderEnginesAreNotGuarded()
    {
        var path = Marker();
        SafeMode.Enter(["fsr1", "blend"], path);
        Assert.False(File.Exists(path));
    }
}

public class EdidTests
{
    private static byte[] Base(int minHz, int maxHz, int extensions = 0)
    {
        var e = new byte[128 * (1 + extensions)];
        e[54] = 0; e[55] = 0; e[57] = 0xFD; e[59] = (byte)minHz; e[60] = (byte)Math.Min(255, maxHz);
        if (maxHz > 255) { e[58] |= 2; e[60] = (byte)(maxHz - 255); }
        e[126] = (byte)extensions;
        return e;
    }

    private static void Cta(byte[] e, params byte[] block)
    {
        e[128] = 0x02; e[129] = 3;
        Array.Copy(block, 0, e, 132, block.Length);
        e[130] = (byte)(4 + block.Length);
    }

    [Fact]
    public void FreeSyncBlockMeansVrr()
    {
        var e = Base(60, 60, 1);
        Cta(e, 0x63, 0x1A, 0x00, 0x00);
        Assert.Equal("FreeSync data block", Platform.Edid.Parse(e, tv: false).Evidence);
    }

    [Fact]
    public void HdmiVrrBlockOnTv()
    {
        var e = Base(24, 120, 1);
        Cta(e, 0x69, 0xD8, 0x5D, 0xC4, 1, 0x78, 0, 0, 40, 120);
        var v = Platform.Edid.Parse(e, tv: true);
        Assert.True(v.Vrr);
        Assert.Equal((40, 120), (v.MinHz, v.MaxHz));
    }

    [Fact]
    public void TvRangeAloneIsNotVrr() => Assert.False(Platform.Edid.Parse(Base(24, 120), tv: true).Vrr);

    [Fact]
    public void FixedMonitorIsNotVrr() => Assert.False(Platform.Edid.Parse(Base(56, 61), tv: false).Vrr);

    [Fact]
    public void WideRangeMonitorIsVrr()
    {
        var v = Platform.Edid.Parse(Base(48, 360), tv: false);
        Assert.True(v.Vrr);
        Assert.Equal(360, v.MaxHz);
    }
}

public class CaptureDeviceTests
{
    private static CaptureDevice Dev(string name, string vid = "0000") =>
        new($@"\\?\usb#vid_{vid}&pid_0001#{name}", name, [new CaptureFormat(1920, 1080, 60, "NV12")]);

    [Theory]
    [InlineData("Game Capture HD60 X", "0fd9")]
    [InlineData("Game Capture HD60 S+", "0fd9")]
    [InlineData("Elgato 4K X", "0fd9")]
    [InlineData("Cam Link 4K", "0fd9")]
    [InlineData("Live Gamer Ultra 2.1-Video", "07ca")]
    [InlineData("Live Gamer Portable 2 Plus", "07ca")]
    [InlineData("AVerMedia HD Capture GC573 1", "0000")]
    [InlineData("ShadowCast 2", "0000")]
    [InlineData("NZXT Signal 4K30 Video", "1e71")]
    [InlineData("XR1 Capture Box", "3842")]
    [InlineData("USB Capture HDMI 4K+", "2935")]
    [InlineData("USB Video", "534d")]
    [InlineData("USB3 Video", "345f")]
    [InlineData("Guermok USB3 Video", "345f")]
    [InlineData("Blackmagic WDM Capture", "1edb")]
    public void KnownCardsScore(string name, string vid) => Assert.True(CaptureCatalog.CardScore(Dev(name, vid)) >= 100);

    [Theory]
    [InlineData("Elgato Facecam", "0fd9")]
    [InlineData("Razer Kiyo Pro", "1532")]
    [InlineData("HD Pro Webcam C920", "046d")]
    [InlineData("USB2.0 HD UVC WebCam", "0bda")]
    [InlineData("Integrated Camera", "04f2")]
    [InlineData("OBS Virtual Camera", "0000")]
    [InlineData("e2eSoft iVCam", "0000")]
    public void WebcamsDoNotScore(string name, string vid) => Assert.Equal(0, CaptureCatalog.CardScore(Dev(name, vid)));

    [Fact]
    public void PicksTheCardOverAnEarlierWebcam()
    {
        var pick = CaptureCatalog.PickDefault([Dev("e2eSoft iVCam"), Dev("Elgato Facecam", "0fd9"), Dev("Game Capture HD60 S+", "0fd9")]);
        Assert.Equal("Game Capture HD60 S+", pick?.Name);
    }

    [Fact]
    public void UnknownCardFoundByVendor() => Assert.True(CaptureCatalog.CardScore(Dev("Some Box", "534d")) > 0);
}