using TretioDLSS.App;

namespace TretioDLSS.Tests;

public class DiscordTests
{
    private static readonly DateTime T0 = new(2026, 11, 22, 18, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void FirstPlayStartsNow()
    {
        var s = DiscordSession.Resume(null, "Grand Theft Auto VI", T0);
        Assert.Equal((T0, T0), (s.StartUtc, s.LastSeenUtc));
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(29.9, true)]
    [InlineData(30, false)]
    [InlineData(240, false)]
    public void ABreakOf30MinutesStartsOver(double breakMinutes, bool keeps)
    {
        var saved = new DiscordSession("Grand Theft Auto VI", T0, T0.AddHours(2));
        var now = saved.LastSeenUtc.AddMinutes(breakMinutes);
        var s = DiscordSession.Resume(saved, "grand theft auto vi", now);
        Assert.Equal(keeps ? T0 : now, s.StartUtc);
        Assert.Equal(now, s.LastSeenUtc);
    }

    [Fact]
    public void AnotherGameStartsOver()
    {
        var saved = new DiscordSession("Grand Theft Auto VI", T0, T0.AddMinutes(50));
        var now = T0.AddMinutes(55);
        Assert.Equal(now, DiscordSession.Resume(saved, "Astro Bot", now).StartUtc);
    }

    [Fact]
    public void SessionSurvivesARestart()
    {
        string path = Path.Combine(Path.GetTempPath(), "tretio-tests", Guid.NewGuid() + ".json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        new DiscordSession("Grand Theft Auto VI", T0, T0.AddMinutes(90)).Save(path);
        var loaded = DiscordSession.Load(path);
        Assert.Equal(new DiscordSession("Grand Theft Auto VI", T0, T0.AddMinutes(90)), loaded);
        Assert.Equal(T0, DiscordSession.Resume(loaded, "Grand Theft Auto VI", T0.AddMinutes(95)).StartUtc);
        Assert.Null(DiscordSession.Load(path + ".missing"));
    }
}
