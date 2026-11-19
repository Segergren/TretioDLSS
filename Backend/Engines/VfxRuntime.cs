using Serilog;
using TretioDLSS.App;
using TretioDLSS.Graphics;

namespace TretioDLSS.Engines;

/// <summary>Finds the NVIDIA Video Effects SDK runtime.</summary>
public static class VfxRuntime
{
    public static string ConfiguredPath { get; set; } = "";

    public static IEnumerable<string> Candidates()
    {
        if (!string.IsNullOrWhiteSpace(ConfiguredPath)) yield return ConfiguredPath;
        // Shipped with the installer (tools/pack.ps1), then downloaded by the app, then dev and SDK installs.
        yield return Path.Combine(AppContext.BaseDirectory, "nvvfx");
        yield return VfxInstaller.InstallDir;
        if (AppInfo.RepoRoot is string root) yield return Path.Combine(root, "ThirdParty", "nvvfx");
        yield return @"C:\Program Files\NVIDIA Corporation\NVIDIA Video Effects";
    }

    /// <summary>Folder that contains the runtime DLL, or null.</summary>
    public static string? Find()
    {
        foreach (var dir in Candidates())
        {
            try
            {
                if (!Directory.Exists(dir)) continue;
                var dll = Directory.EnumerateFiles(dir, "NVVideoEffects.dll", SearchOption.AllDirectories).FirstOrDefault();
                if (dll != null) return Path.GetDirectoryName(dll);
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "VFX runtime probe failed for {Dir}", dir);
            }
        }
        return null;
    }

    /// <summary>Runtime folder that has everything the engine needs (vfx-vfg or vfx-vsr), or null.</summary>
    public static string? FindFor(string engineId)
    {
        string sdkFeature = engineId == "vfx-vfg" ? "nvvfxvideoframegeneration" : "nvvfxvideosuperres";
        foreach (var dir in Candidates())
        {
            try
            {
                if (!Directory.Exists(dir)) continue;
                if (VfxInstaller.IsInstalledIn(dir, engineId)) return dir;
                var dll = Directory.EnumerateFiles(dir, "NVVideoEffects.dll", SearchOption.AllDirectories).FirstOrDefault();
                if (dll != null && Features(Path.GetDirectoryName(dll)!).Contains(sdkFeature, StringComparer.OrdinalIgnoreCase))
                    return Path.GetDirectoryName(dll);
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "VFX runtime probe failed for {Dir}", dir);
            }
        }
        return null;
    }

    public static bool HasFeature(string engineId) => FindFor(engineId) != null;

    /// <summary>Installed feature names, for example nvvfxvideoframegeneration.</summary>
    public static List<string> Features(string runtimeDir)
    {
        try
        {
            var root = Directory.GetParent(runtimeDir)?.FullName ?? runtimeDir;
            return Directory.EnumerateDirectories(Path.Combine(root, "features"))
                .Select(Path.GetFileName)
                .Where(n => n != null)
                .Cast<string>()
                .ToList();
        }
        catch
        {
            return [];
        }
    }
}
