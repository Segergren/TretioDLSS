using System.Text.Json;
using TretioDLSS.App;
using TretioDLSS.Core;
using TretioDLSS.Engines;

namespace TretioDLSS.Tests;

[Collection("SafeMode")]
public class NeuralTests
{
    private static string TempFile() => Path.Combine(Path.GetTempPath(), "tretio-tests", Guid.NewGuid() + ".json");

    [Fact]
    public void SettingsKeepDlss5AndClampIt()
    {
        string path = TempFile();
        var svc = new SettingsService(path);
        svc.Load();
        Assert.Equal("off", svc.Current.Neural.Engine);
        using var doc = JsonDocument.Parse("""{"neural":{"engine":"dlss5","layers":9,"intensity":4,"style":"natural","model":"bogus","placement":"output"}}""");
        svc.ApplyPatch(doc.RootElement);
        var n = svc.Current.Neural;
        Assert.Equal(("dlss5", 4, 1.0, "natural", "fast", "output"), (n.Engine, n.Layers, n.Intensity, n.Style, n.Model, n.Placement));
        svc.Save();
        var again = new SettingsService(path);
        again.Load();
        Assert.Equal(4, again.Current.Neural.Layers);
        Assert.Equal("dlss5", Profiles.Find(again.Current, again.Current.ActiveProfile)!.Neural.Engine);
    }

    [Fact]
    public void ProfilesCarryTheLayers()
    {
        var svc = new SettingsService(TempFile());
        svc.Load();
        svc.Mutate(s => { s.Neural.Engine = "dlss5"; s.Neural.Layers = 3; });
        svc.Mutate(s =>
        {
            s.Profiles.Add(Profiles.Capture(s, "Plain"));
            s.ActiveProfile = "Plain";
        });
        svc.Mutate(s => s.Neural.Engine = "off");
        svc.Mutate(s => Profiles.Apply(s, Profiles.Find(s, Profiles.DefaultName)!));
        Assert.Equal(("dlss5", 3), (svc.Current.Neural.Engine, svc.Current.Neural.Layers));
        Assert.Contains("DLSS 5 with 3 layers", Profiles.Summary(Profiles.Find(svc.Current, Profiles.DefaultName)!));
        Assert.DoesNotContain("DLSS 5", Profiles.Summary(Profiles.Find(svc.Current, "Plain")!));
    }

    [Theory]
    [InlineData("off", 3, "natural", "fast", "source", 0, 1, 2, true)]
    [InlineData("dlss5", 2, "cinematic", "fast", "source", 2, 2, 2, true)]
    [InlineData("dlss5", 7, "default", "original", "output", 4, 0, 1, false)]
    [InlineData("dlss5", 0, "unknown", "fast", "anything", 1, 0, 2, true)]
    public void ConfigFromSettingsWords(string engine, int layers, string style, string model, string placement,
        int wantLayers, int wantStyle, int wantPreset, bool wantBefore)
    {
        var c = NeuralConfig.From(engine, layers, style, 0.5, model, placement);
        Assert.Equal((wantLayers, wantStyle, wantPreset, wantBefore), (c.Layers, c.Style, c.Preset, c.BeforeUpscale));
        Assert.Equal(wantLayers > 0, c.Enabled);
        Assert.Equal(0.5, c.Intensity);
    }

    [Fact]
    public void RuntimeBuildFollowsTheGpu()
    {
        Assert.Same(NeuralRuntime.Community, NeuralRuntime.For(0x190));
        Assert.Same(NeuralRuntime.Community, NeuralRuntime.For(null));
        Assert.Same(NeuralRuntime.Nvidia, NeuralRuntime.For(0x1B0));
        Assert.True(NeuralRuntime.DriverOk("617.42"));
        Assert.True(NeuralRuntime.DriverOk("616.56"));
        Assert.False(NeuralRuntime.DriverOk("610.62"));
        Assert.True(NeuralRuntime.DriverOk(""));
        Assert.NotEqual(NeuralRuntime.Nvidia.Size, NeuralRuntime.Community.Size);
    }

    [Fact]
    public void CatalogOffersOneToFourLayers()
    {
        var none = Assert.Single(EngineCatalog.Neural(new Capabilities()), e => e.Id == NeuralRuntime.Id);
        Assert.False(none.Available);
        Assert.Equal("Needs an NVIDIA RTX GPU", none.Reason);
        var older = Assert.Single(EngineCatalog.Neural(new Capabilities { Nvidia = true }), e => e.Id == NeuralRuntime.Id);
        Assert.Equal("Needs an RTX 40 or 50 series GPU", older.Reason);
        Assert.Equal([1, 2, 3, 4], older.Multipliers!);
        Assert.Equal("DLSS 5", EngineCatalog.Name(NeuralRuntime.Id));
    }

    /// <summary>The live path: layers and model start a new session while the pipeline runs, style and intensity do not.</summary>
    [Fact]
    [Trait("Category", "Gpu")]
    public void LayersChangeWhileRunning()
    {
        if (NvApiArchitecture() is not uint arch || !NeuralRuntime.IsInstalledFor(arch)) Assert.Skip("Needs an RTX 40 or 50 GPU and the DLSS 5 runtime (--install dlss5)");
        if (NeuralRuntime.BridgeMissing() is string missing) Assert.Skip(missing);
        SafeMode.ResetForTests();
        string dataFolder = AppInfo.DataFolder;
        AppInfo.DataFolder = Path.Combine(Path.GetTempPath(), "tretio-tests", Guid.NewGuid().ToString());
        Directory.CreateDirectory(AppInfo.DataFolder);
        using var host = new Pipeline.PipelineHost(new Pipeline.PipelineConfig
        {
            SourceFactory = SourceFactory.FromSpec("pattern:30in60"),
            Neural = NeuralConfig.From("dlss5", 1, "default", 1, "fast", "source"),
            OffscreenWidth = 1280,
            OffscreenHeight = 720,
            PreviewEnabled = false,
        });
        host.Start();
        try
        {
            Assert.True(WaitFor(() => host.NeuralLine().StartsWith("1 layer,"), 15000), host.NeuralLine());
            Assert.True(host.SetNeural(NeuralConfig.From("dlss5", 3, "cinematic", 0.5, "fast", "source")));
            Assert.True(WaitFor(() => host.NeuralLine().StartsWith("3 layers, Cinematic"), 15000), host.NeuralLine());
            Assert.True(host.SetNeural(NeuralConfig.Off));
            Assert.True(WaitFor(() => host.NeuralLine() == "Off", 5000), host.NeuralLine());
            Assert.Equal(Pipeline.PipelineStatus.Running, host.Status);
            Assert.Equal(0, Interlocked.Read(ref host.Counters.Dropped));
        }
        finally
        {
            host.Stop();
            AppInfo.DataFolder = dataFolder;
        }
    }

    private static uint? NvApiArchitecture()
    {
        try { return Nvidia.NvApi.Instance?.Architecture(); }
        catch { return null; }
    }

    private static bool WaitFor(Func<bool> condition, int ms)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms)
        {
            if (condition()) return true;
            Thread.Sleep(50);
        }
        return condition();
    }

    [Fact]
    public void Dlss5IsGuardedBySafeMode()
    {
        SafeMode.ResetForTests();
        string path = Path.Combine(Path.GetTempPath(), "tretio-tests", Guid.NewGuid() + ".marker");
        SafeMode.Enter(["fsr1", "off", NeuralRuntime.Id], path);
        Assert.StartsWith(NeuralRuntime.Id + "|", File.ReadAllText(path));
        File.WriteAllText(path, NeuralRuntime.Id + "|999999");
        SafeMode.CheckAtStartup(path);
        Assert.True(SafeMode.IsDisabled(NeuralRuntime.Id));
        Assert.Contains("DLSS 5 is off for this session", SafeMode.Message(NeuralRuntime.Id));
        SafeMode.ResetForTests();
    }
}
