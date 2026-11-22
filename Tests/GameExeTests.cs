using System.Text;
using TretioDLSS.App;

namespace TretioDLSS.Tests;

public class GameExeTests
{
    [Theory]
    [InlineData("Grand Theft Auto VI", null, "Grand Theft Auto VI.exe")]
    [InlineData("Grand Theft Auto V Enhanced", "grand theft auto v enhanced/gta5_enhanced.exe", @"grand theft auto v enhanced\gta5_enhanced.exe")]
    [InlineData("Astro Bot", ">astrobot.exe", "astrobot.exe")]
    [InlineData("What: A Game?", "../evil/notanexe", "What A Game.exe")]
    [InlineData("???", null, "Game.exe")]
    public void ExeNameFollowsDiscordThenTheGame(string game, string? discordExe, string expected) =>
        Assert.Equal(expected, GameExe.RelativePath(game, discordExe));

    [Fact]
    public void LauncherPointsAtTheInstalledDll()
    {
        var exe = new byte[4096];
        byte[] slot = Encoding.UTF8.GetBytes("TretioDLSS.dll");
        slot.CopyTo(exe, 1000);
        GameExe.PatchDllPath(exe, @"..\..\app\TretioDLSS.dll");
        Assert.Equal(@"..\..\app\TretioDLSS.dll", Encoding.UTF8.GetString(exe, 1000, 24));
        Assert.All(exe.Skip(1024).Take(1000), b => Assert.Equal(0, b));
        Assert.Throws<InvalidOperationException>(() => GameExe.PatchDllPath(new byte[2048], "x.dll"));
    }

    [Fact]
    public void VersionInfoNamesTheGame()
    {
        byte[] v = GameExe.VersionInfo("Grand Theft Auto VI", "Grand Theft Auto VI.exe");
        Assert.Equal(v.Length, BitConverter.ToUInt16(v, 0));
        string text = Encoding.Unicode.GetString(v);
        Assert.Contains("FileDescription\0\0Grand Theft Auto VI\0", text.Replace("\0\0\0", "\0\0"));
        Assert.Contains("OriginalFilename", text);
    }
}
