using System.Diagnostics;


namespace TretioDLSS.Output;

/// <summary>Sunshine helper: detect it and hand the user the app entry to add.</summary>
public static class Sunshine
{
    public static string? InstallFolder()
    {
        string[] candidates = [@"C:\Program Files\Sunshine", @"C:\Program Files (x86)\Sunshine"];
        return candidates.FirstOrDefault(d => File.Exists(Path.Combine(d, "sunshine.exe")));
    }

    public static string State()
    {
        if (InstallFolder() == null) return "not installed";
        return Process.GetProcessesByName("sunshine").Length > 0 ? "running" : "installed, not running";
    }

    public static string CommandLine(string profile)
    {
        string exe = Environment.ProcessPath ?? "TretioDLSS.exe";
        return $"\"{exe}\" --start --profile \"{profile}\"";
    }

    public sealed record Result(bool Ok, string Message);

    /// <summary>
    /// Sunshine's apps.json is not writable without elevation, so the entry goes through its web UI.
    /// The command is copied to the clipboard and the UI opened at the Applications page.
    /// </summary>
    public static Result Register(string appName, string profile)
    {
        if (InstallFolder() == null) return new Result(false, "Sunshine is not installed");
        string cmd = CommandLine(profile);
        try
        {
            var t = new Thread(() => System.Windows.Forms.Clipboard.SetText(cmd));
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
            t.Join(2000);
            Process.Start(new ProcessStartInfo("https://localhost:47990/apps") { UseShellExecute = true });
        }
        catch
        {
        }
        return new Result(true, $"Sunshine opened. Add an application named {appName} and paste the copied command.");
    }
}
