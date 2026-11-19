using Serilog;
using Velopack;
using Velopack.Sources;

namespace TretioDLSS.App;

/// <summary>Updates through Velopack from the GitHub releases in AppInfo.UpdateSource. A downloaded update installs silently on exit.</summary>
public sealed class UpdateService
{
    private UpdateInfo? _pending;

    public string Status { get; private set; } = "";
    public string? PendingVersion => _pending?.TargetFullRelease.Version.ToString();

    private static UpdateManager Manager() => new(new GithubSource(AppInfo.UpdateSource, null, false));

    public async Task<string> CheckAsync()
    {
        try
        {
            var mgr = Manager();
            if (!mgr.IsInstalled) return Status = "Updates work in the installed app only";
            var info = await mgr.CheckForUpdatesAsync();
            if (info == null)
            {
                _pending = null;
                return Status = $"Up to date, version {mgr.CurrentVersion}";
            }
            await mgr.DownloadUpdatesAsync(info);
            _pending = info;
            Log.Information("Update {Version} downloaded", info.TargetFullRelease.Version);
            return Status = $"Version {info.TargetFullRelease.Version} installs when you exit, or restart now.";
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Update check failed");
            return Status = "Update check failed: " + ex.Message;
        }
    }

    public void InstallAndRestart()
    {
        if (_pending == null) return;
        Log.Information("Installing update {Version}", _pending.TargetFullRelease.Version);
        Manager().ApplyUpdatesAndRestart(_pending.TargetFullRelease);
    }

    /// <summary>Hands a downloaded update to Velopack, which installs it silently once this process has exited.</summary>
    public void ApplyOnExit()
    {
        if (_pending == null) return;
        try
        {
            Log.Information("Update {Version} installs after exit", _pending.TargetFullRelease.Version);
            Manager().WaitExitThenApplyUpdates(_pending.TargetFullRelease, silent: true, restart: false);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Update on exit failed");
        }
    }
}
