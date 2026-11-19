using Serilog;
using TretioDLSS.Engines;
using TretioDLSS.Graphics;
using TretioDLSS.Nvidia;
using TretioDLSS.Platform;
using Vortice.DXGI;

namespace TretioDLSS.App;

public sealed record CapabilityRow(string Group, string Name, string Value, bool Ok);

/// <summary>What this machine can do. Runs after the driver profile reconcile.</summary>
public sealed class Capabilities
{
    public string GpuName { get; private set; } = "";
    public bool Nvidia { get; internal set; }
    public bool Ada { get; private set; }
    public string Driver { get; private set; } = "";
    public string? VfxRuntime { get; private set; }
    public bool RifeReady { get; private set; }
    /// <summary>The pipeline runs on an AMD GPU (no NVIDIA one, or chosen in Advanced).</summary>
    public bool AmdPipeline { get; private set; }
    public string? AmfVersion { get; private set; }
    public bool Tearing { get; private set; }
    public bool HighRateCapture { get; private set; }
    public int WindowsBuild { get; private set; }
    public List<CapabilityRow> Rows { get; } = new();

    private static string CleanName(string name) => name.Replace("(TM)", "").Replace("(R)", "").Replace("  ", " ").Trim();

    public static Capabilities Detect()
    {
        var c = new Capabilities();
        c.WindowsBuild = Environment.OSVersion.Version.Build;
        c.HighRateCapture = c.WindowsBuild >= 26100;
        try
        {
            using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory6>();
            using (var f5 = factory.QueryInterfaceOrNull<IDXGIFactory5>()) c.Tearing = f5?.PresentAllowTearing ?? false;
            var adapters = new List<string>();
            bool amdFirst = false;
            for (uint i = 0; factory.EnumAdapterByGpuPreference(i, GpuPreference.HighPerformance, out IDXGIAdapter1? a).Success; i++)
            {
                if (a == null) break;
                using (a)
                {
                    var d = a.Description1;
                    if ((d.Flags & AdapterFlags.Software) != 0) continue;
                    adapters.Add(CleanName(d.Description));
                    if (adapters.Count == 1 && d.VendorId == 0x1002) amdFirst = true;
                    if (d.VendorId == 0x10DE && !c.Nvidia)
                    {
                        c.Nvidia = true;
                        c.GpuName = CleanName(d.Description);
                    }
                }
            }
            if (!c.Nvidia && adapters.Count > 0) c.GpuName = adapters[0];
            c.AmdPipeline = !c.Nvidia && amdFirst;
            c.Rows.Add(new("GPU", "Graphics card", c.GpuName, true));
            foreach (var other in adapters.Where(a => a != c.GpuName))
                c.Rows.Add(new("GPU", "Other GPU", other + ", not used", true));
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Adapter enumeration failed");
        }

        if (c.Nvidia)
        {
            var nv = NvApi.Instance;
            c.Driver = nv?.DriverVersion() ?? "";
            var arch = nv?.Architecture();
            // RTX 40 is Ada (0x190), RTX 50 is Blackwell (0x1B0 and up).
            c.Ada = arch is uint a2 && a2 >= 0x190 || c.GpuName.Contains("RTX 40") || c.GpuName.Contains("RTX 50");
            c.Rows.Add(new("NVIDIA", "Driver", string.IsNullOrEmpty(c.Driver) ? "Unknown" : c.Driver, !string.IsNullOrEmpty(c.Driver)));
        }
        else
        {
            c.Rows.Add(new("NVIDIA", "NVIDIA features", "No NVIDIA GPU found", false));
        }

        c.VfxRuntime = TretioDLSS.Engines.VfxRuntime.Find();
        foreach (var (id, name) in new[] { ("vfx-vsr", "RTX Video"), ("vfx-vfg", "NVIDIA Frame Generation") })
        {
            var dir = TretioDLSS.Engines.VfxRuntime.FindFor(id);
            c.Rows.Add(new("NVIDIA", name, dir == null ? "Not downloaded yet" : $"Ready, from {dir}", dir != null));
        }
        c.AmfVersion = AmfFrameGenerator.RuntimeVersion();
        c.Rows.Add(new("Engines", "AMD Frame Generation", c.AmfVersion == null ? "No AMD driver (AMF runtime missing)" : c.AmdPipeline ? $"Ready, AMF {c.AmfVersion}" : $"AMF {c.AmfVersion} found, but the picture runs on {c.GpuName}", c.AmfVersion != null && c.AmdPipeline));
        var rife = RifeFrameGenerator.Models();
        c.RifeReady = rife.Count > 0 && RifeFrameGenerator.RuntimeAvailable();
        c.Rows.Add(new("Engines", "RIFE", rife.Count == 0 ? "No models found" : !c.RifeReady ? "ONNX Runtime with DirectML did not load" : $"Ready: {string.Join(", ", rife.Select(RifeFrameGenerator.ModelLabel))}", c.RifeReady));

        c.Rows.Add(new("Windows", "Version", $"Build {c.WindowsBuild}", true));
        c.Rows.Add(new("Windows", "Tearing presents", c.Tearing ? "Supported" : "Not supported", c.Tearing));
        c.Rows.Add(new("Windows", "Capture above 60 fps", c.HighRateCapture ? "Supported" : "Needs Windows 11 24H2", c.HighRateCapture));
        var privacy = Privacy.Check();
        c.Rows.Add(new("Windows", "Camera access", privacy.CameraAllowed ? "Allowed for desktop apps" : "Blocked for desktop apps", privacy.CameraAllowed));
        c.Rows.Add(new("Windows", "Microphone access", privacy.MicrophoneAllowed ? "Allowed for desktop apps" : "Blocked for desktop apps", privacy.MicrophoneAllowed));
        return c;
    }

}
