using Serilog;
using TretioDLSS.App;

namespace TretioDLSS.Engines;

/// <summary>
/// Crash guard for vendor engines: a marker names the engine while it runs; if the
/// marker is still there at the next start, that engine stays off for the session.
/// </summary>
public static class SafeMode
{
    private static readonly HashSet<string> Guarded = ["vfx-vfg", "vfx-vsr", "rife", "dlss-fg", NeuralRuntime.Id];

    /// <summary>The engine the last run died in, disabled for this session; null when all is well.</summary>
    public static string? Disabled { get; private set; }

    /// <summary>After this long without trouble the marker is cleared.</summary>
    public const long StableMs = 30_000;

    public const string ShortMessage = "Off for this session";

    public static string Message(string engine) =>
        $"{EngineCatalog.Name(engine)} is off for this session: the last run stopped inside it. Pick it again to retry.";

    public static void CheckAtStartup(string? markerPath = null)
    {
        string path = markerPath ?? AppInfo.SafeModeMarker;
        try
        {
            if (!File.Exists(path)) return;
            var parts = File.ReadAllText(path).Trim().Split('|');
            string engine = parts[0];
            // The marker of a process that is still running (another instance, a headless run) is not a crash.
            if (parts.Length > 1 && int.TryParse(parts[1], out int pid) && pid != Environment.ProcessId && IsAlive(pid)) return;
            File.Delete(path);
            if (engine.Length == 0) return;
            Disabled = engine;
            Log.Warning("Safe mode: the last run stopped inside {Engine}, it stays off for this session", engine);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Safe mode marker unreadable");
        }
    }

    public static bool IsDisabled(string engine) => Disabled == engine;

    private static bool IsAlive(int pid)
    {
        try
        {
            using var p = System.Diagnostics.Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>The user switched the engine back on explicitly: that is the retry.</summary>
    public static void Retry(string engine)
    {
        if (Disabled != engine) return;
        Disabled = null;
        Log.Information("Safe mode: {Engine} switched back on by the user", engine);
    }

    public static void Enter(IEnumerable<string> engines, string? markerPath = null)
    {
        var guarded = engines.Where(Guarded.Contains).ToList();
        if (guarded.Count == 0) return;
        try
        {
            string path = markerPath ?? AppInfo.SafeModeMarker;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, guarded[^1] + "|" + Environment.ProcessId);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Safe mode marker not written");
        }
    }

    /// <summary>Removes this process's marker; a marker written by another live process stays.</summary>
    public static void Clear(string? markerPath = null)
    {
        string path = markerPath ?? AppInfo.SafeModeMarker;
        try
        {
            if (!File.Exists(path)) return;
            var parts = File.ReadAllText(path).Trim().Split('|');
            if (parts.Length > 1 && parts[1] != Environment.ProcessId.ToString()) return;
            File.Delete(path);
        }
        catch { }
    }

    internal static void ResetForTests() => Disabled = null;
}
