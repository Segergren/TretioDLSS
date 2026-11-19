using System.Runtime.InteropServices;
using Vortice.DXGI;
using static TretioDLSS.Platform.Native;

namespace TretioDLSS.Platform;

public sealed record MonitorInfo(
    string Id,
    string Name,
    int Width,
    int Height,
    double Refresh,
    bool Vrr,
    bool Hdr,
    bool Primary,
    bool Tv,
    string Adapter,
    int X,
    int Y,
    IntPtr Handle,
    string GdiName);

public static class Monitors
{
    /// <summary>Every active display with its DXGI adapter and friendly name.</summary>
    public static List<MonitorInfo> Enumerate()
    {
        var names = FriendlyNames();
        var list = new List<MonitorInfo>();
        using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory6>();
        for (uint a = 0; factory.EnumAdapters1(a, out var adapter).Success; a++)
        {
            using (adapter)
            {
                string adapterName = adapter.Description1.Description;
                for (uint o = 0; adapter.EnumOutputs(o, out var output).Success; o++)
                {
                    using (output)
                    {
                        using var output6 = output.QueryInterfaceOrNull<IDXGIOutput6>();
                        var desc = output.Description;
                        bool hdr = false;
                        if (output6 != null)
                        {
                            var d1 = output6.Description1;
                            hdr = d1.ColorSpace == ColorSpaceType.RgbFullG2084NoneP2020;
                        }
                        var mi = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
                        GetMonitorInfo(desc.Monitor, ref mi);
                        var dm = new DEVMODE { dmSize = (short)Marshal.SizeOf<DEVMODE>() };
                        EnumDisplaySettings(desc.DeviceName, ENUM_CURRENT_SETTINGS, ref dm);
                        names.TryGetValue(desc.DeviceName, out var friendly);
                        string name = string.IsNullOrWhiteSpace(friendly) ? desc.DeviceName.Replace(@"\\.\", "") : friendly;
                        bool tv = LooksLikeTv(name);
                        var vrr = VrrOf(desc.DeviceName, tv);
                        list.Add(new MonitorInfo(
                            Id: StableId(desc.DeviceName, name),
                            Name: name,
                            Width: mi.rcMonitor.Width,
                            Height: mi.rcMonitor.Height,
                            Refresh: dm.dmDisplayFrequency,
                            Vrr: vrr.Vrr,
                            Hdr: hdr,
                            Primary: (mi.dwFlags & 1) != 0,
                            Tv: tv,
                            Adapter: adapterName,
                            X: mi.rcMonitor.Left,
                            Y: mi.rcMonitor.Top,
                            Handle: desc.Monitor,
                            GdiName: desc.DeviceName));
                    }
                }
            }
        }
        return list;
    }

    private static string StableId(string gdi, string name) => $"{name}|{gdi}";

    public static MonitorInfo? Find(List<MonitorInfo> monitors, string id)
    {
        if (string.IsNullOrEmpty(id)) return monitors.FirstOrDefault(m => m.Primary) ?? monitors.FirstOrDefault();
        return monitors.FirstOrDefault(m => m.Id == id)
            ?? monitors.FirstOrDefault(m => m.Name == id.Split('|')[0])
            ?? monitors.FirstOrDefault(m => m.Primary)
            ?? monitors.FirstOrDefault();
    }

    internal static bool LooksLikeTv(string name)
    {
        string n = name.ToUpperInvariant();
        string[] marks = ["TV", "BRAVIA", "OLED48", "OLED55", "OLED65", "OLED77", "OLED83", "QN9", "QN8", "Q8", "Q9", "S9", "SAMSUNG TV", "LG TV", "HISENSE", "TCL", "PHILIPS TV", "VIZIO"];
        return marks.Any(m => n == m || n.Contains(m + " ") || n.EndsWith(" " + m) || n.StartsWith(m));
    }

    private static readonly Dictionary<string, string> _devicePaths = new(StringComparer.OrdinalIgnoreCase);

    private static Edid.VrrInfo VrrOf(string gdiName, bool tv)
    {
        if (!_devicePaths.TryGetValue(gdiName, out var path) || Edid.Read(path) is not byte[] edid) return new(false, 0, 0, "no EDID");
        return Edid.Parse(edid, tv);
    }

    private static Dictionary<string, string> FriendlyNames()
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, out uint np, out uint nm) != 0) return result;
            var paths = new DISPLAYCONFIG_PATH_INFO[np];
            var modes = new DISPLAYCONFIG_MODE_INFO[nm];
            if (QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, ref np, paths, ref nm, modes, IntPtr.Zero) != 0) return result;
            for (int i = 0; i < np; i++)
            {
                var src = new DISPLAYCONFIG_SOURCE_DEVICE_NAME();
                src.header.type = 1;
                src.header.size = Marshal.SizeOf<DISPLAYCONFIG_SOURCE_DEVICE_NAME>();
                src.header.adapterId = paths[i].sourceAdapterId;
                src.header.id = paths[i].sourceId;
                if (DisplayConfigGetDeviceInfo(ref src) != 0) continue;
                var tgt = new DISPLAYCONFIG_TARGET_DEVICE_NAME();
                tgt.header.type = 2;
                tgt.header.size = Marshal.SizeOf<DISPLAYCONFIG_TARGET_DEVICE_NAME>();
                tgt.header.adapterId = paths[i].targetAdapterId;
                tgt.header.id = paths[i].targetId;
                if (DisplayConfigGetDeviceInfo(ref tgt) != 0) continue;
                result[src.viewGdiDeviceName] = tgt.monitorFriendlyDeviceName;
                _devicePaths[src.viewGdiDeviceName] = tgt.monitorDevicePath;
            }
        }
        catch
        {
        }
        return result;
    }
}
