namespace TretioDLSS.Capture;

public sealed record CaptureFormat(int Width, int Height, double Fps, string Subtype)
{
    public bool Uncompressed => Subtype is "NV12" or "YUY2";
    public string Key => $"{Width}x{Height}@{Fps:0.###}:{Subtype}";
}

/// <summary>A capture device. DshowOnly: Media Foundation could not read it, so DirectShow is used.</summary>
public sealed record CaptureDevice(string Id, string Name, List<CaptureFormat> Formats, bool DshowOnly = false);

public static class FormatPicker
{
    /// <summary>Rates within 1% match (59.94 counts as 60).</summary>
    public static bool RateMatches(double a, double b) => Math.Abs(a - b) <= Math.Max(a, b) * 0.01;

    /// <summary>
    /// Default capture format: uncompressed NV12 before YUY2 at 1920 x 1080 and
    /// 60 Hz, else the largest uncompressed format at 60 Hz or more. MJPEG is never auto-selected.
    /// </summary>
    public static CaptureFormat? PickDefault(IEnumerable<CaptureFormat> formats)
    {
        var list = formats.Where(f => f.Uncompressed).ToList();
        int Pref(CaptureFormat f) => f.Subtype == "NV12" ? 0 : 1;
        var exact = list.Where(f => f.Width == 1920 && f.Height == 1080 && RateMatches(f.Fps, 60)).OrderBy(Pref).FirstOrDefault();
        if (exact != null) return exact;
        return list.Where(f => f.Fps >= 59)
                .OrderByDescending(f => (long)f.Width * f.Height)
                .ThenBy(f => Math.Abs(f.Fps - 60))
                .ThenBy(Pref)
                .FirstOrDefault()
            ?? list.OrderByDescending(f => f.Fps).ThenByDescending(f => (long)f.Width * f.Height).FirstOrDefault();
    }

    /// <summary>The device format closest to what the settings ask for.</summary>
    public static CaptureFormat? Find(IEnumerable<CaptureFormat> formats, int w, int h, double fps, string subtype) =>
        formats.FirstOrDefault(f => f.Width == w && f.Height == h && RateMatches(f.Fps, fps) && f.Subtype == subtype)
        ?? formats.FirstOrDefault(f => f.Width == w && f.Height == h && RateMatches(f.Fps, fps) && f.Uncompressed);
}
