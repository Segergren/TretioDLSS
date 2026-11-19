using FlashCap;
using Serilog;

namespace TretioDLSS.Capture;

/// <summary>Capture devices from both APIs, merged by name.</summary>
public static class CaptureCatalog
{
    public static List<CaptureDevice> Enumerate()
    {
        List<CaptureDevice> mf = [], ds = [];
        try { mf = UvcSource.Enumerate(); } catch (Exception ex) { Log.Warning(ex, "Media Foundation device list failed"); }
        try { ds = DirectShow(); } catch (Exception ex) { Log.Warning(ex, "DirectShow device list failed"); }
        var result = new List<CaptureDevice>();
        foreach (var d in mf)
        {
            var twin = ds.FirstOrDefault(x => x.Name == d.Name);
            if (d.Formats.Count == 0 && twin != null && twin.Formats.Count > 0)
            {
                Log.Information("{Name}: no formats through Media Foundation, using DirectShow", d.Name);
                result.Add(twin with { Id = d.Id });
            }
            else result.Add(d);
        }
        foreach (var d in ds)
            if (!mf.Any(x => x.Name == d.Name)) result.Add(d);
        return result;
    }

    /// <summary>Name parts of known capture cards. Bare brand names are left out: Elgato, AVerMedia and Razer also make webcams.</summary>
    private static readonly string[] CardNames =
    [
        "game capture", "cam link", "elgato 4k", "live gamer", "avermedia hd capture", "avermedia gc5", "extremecap", "x'tra go",
        "shadowcast", "nzxt signal", "xr1 capture", "ripsaw", "usb capture hdmi", "decklink", "blackmagic", "intensity",
        "ultrastudio", "hd pvr", "hauppauge", "guermok", "usb3 video", "usb2 video", "usb3.0 uhd", "usb3.0 video", "usb3.0 capture",
        "usb3. 0 capture", "hdmi to u3 capture", "hdmi capture", "fhd capture", "video capture",
    ];

    /// <summary>USB vendors that make capture cards and no webcams: MacroSilicon, UltraSemi (MS213x), Magewell, Hauppauge, EVGA, NZXT, Epiphan, ezcap.</summary>
    private static readonly string[] CardVendors = ["534d", "345f", "2935", "2040", "3842", "1e71", "5555", "32ed"];

    /// <summary>How sure the name or USB vendor makes it that a device is a game capture card; 0 when nothing points to one.</summary>
    public static int CardScore(CaptureDevice d)
    {
        string n = d.Name.Trim().ToLowerInvariant();
        string id = d.Id.ToLowerInvariant();
        int score = 0;
        if (CardNames.Any(n.Contains) || n == "usb video") score += 100;
        if (CardVendors.Any(v => id.Contains("usb#vid_" + v))) score += 50;
        if (score > 0 && d.Formats.Any(f => f.Uncompressed && f.Width >= 1920 && f.Fps >= 59)) score += 10;
        return score;
    }

    /// <summary>The device to use when none is chosen: the most likely capture card, else a USB device with 1080p60 uncompressed, else the first.</summary>
    public static CaptureDevice? PickDefault(IReadOnlyList<CaptureDevice> devices) =>
        devices.OrderByDescending(CardScore)
            .ThenByDescending(d => d.Id.Contains("usb#", StringComparison.OrdinalIgnoreCase) && d.Formats.Any(f => f.Uncompressed && f.Width >= 1920 && f.Fps >= 59))
            .FirstOrDefault();

    public static List<CaptureDevice> DirectShow()
    {
        var list = new List<CaptureDevice>();
        foreach (var d in new CaptureDevices().EnumerateDescriptors().Where(x => x.DeviceType == DeviceTypes.DirectShow))
        {
            var formats = d.Characteristics
                .Select(c => Subtype(c.PixelFormat) is string sub
                    ? new CaptureFormat(c.Width, c.Height, Math.Round((double)c.FramesPerSecond.Numerator / Math.Max(1, c.FramesPerSecond.Denominator), 3), sub)
                    : null)
                .OfType<CaptureFormat>()
                .Distinct()
                .OrderByDescending(f => (long)f.Width * f.Height).ThenByDescending(f => f.Fps).ThenBy(f => f.Subtype)
                .ToList();
            list.Add(new CaptureDevice("dshow:" + d.Name, d.Name, formats, DshowOnly: true));
        }
        return list;
    }

    private static string? Subtype(PixelFormats f) => f switch
    {
        PixelFormats.YUYV => "YUY2",
        PixelFormats.NV12 => "NV12",
        _ => null,
    };
}
