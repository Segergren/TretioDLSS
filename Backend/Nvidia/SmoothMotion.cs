using System.Diagnostics;

namespace TretioDLSS.Nvidia;

/// <summary>
/// NVIDIA Smooth Motion is not used by the app. If someone turns it on for our exe in the NVIDIA
/// app, the driver generates frames on top of ours; this only detects that.
/// </summary>
public static class SmoothMotion
{
    /// <summary>True when the driver's Smooth Motion present layer is loaded in this process.</summary>
    public static bool PresentLayerLoaded()
    {
        try
        {
            return Process.GetCurrentProcess().Modules.Cast<ProcessModule>()
                .Any(m => m.ModuleName.Equals("NvPresent64.dll", StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return false;
        }
    }
}
