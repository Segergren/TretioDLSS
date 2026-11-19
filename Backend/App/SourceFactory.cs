using TretioDLSS.Capture;
using TretioDLSS.Core;
using TretioDLSS.Pipeline;

namespace TretioDLSS.App;

public static class SourceFactory
{
    /// <summary>"pattern:30in60[:yuy2]", "file:C:\clip.mp4[@60]", "raw:dir", "uvc[:name]", "window:title".</summary>
    public static Func<IVideoSource> FromSpec(string spec, CommandLine? cli = null, string api = "auto")
    {
        int colon = spec.IndexOf(':');
        string kind = colon < 0 ? spec : spec[..colon];
        string arg = colon < 0 ? "" : spec[(colon + 1)..];
        switch (kind)
        {
            case "pattern":
            {
                var o = PatternOptions.Parse(string.IsNullOrEmpty(arg) ? "30in60" : arg);
                if (cli != null)
                    o = o with { JitterMs = cli.JitterMs, DropRealProbability = cli.DropRate, RepeatNoise = cli.RepeatNoise, SceneCutEvery = cli.SceneCutEvery };
                return () => new TestPatternSource(o);
            }
            case "black":
                return () => new TestPatternSource(PatternOptions.Parse("60in60") with { Black = true });
            case "file":
            {
                int container = 60;
                int at = arg.LastIndexOf('@');
                if (at > 2 && int.TryParse(arg[(at + 1)..], out var c))
                {
                    container = c;
                    arg = arg[..at];
                }
                return () => new FileSource(arg, container);
            }
            case "raw":
                return () => new RawDumpSource(arg) { Loop = cli?.Headless != true };
            case "uvc":
            {
                var devices = CaptureCatalog.Enumerate();
                var dev = string.IsNullOrEmpty(arg) ? CaptureCatalog.PickDefault(devices) :devices.FirstOrDefault(d => d.Name.Contains(arg, StringComparison.OrdinalIgnoreCase) || d.Id == arg);
                if (dev == null) throw new InvalidOperationException("No capture device found" + (arg.Length > 0 ? $" matching \"{arg}\"" : ""));
                var fmt = (cli?.CaptureFormat is string want ? dev.Formats.FirstOrDefault(f => f.Key == want) : null) ?? FormatPicker.PickDefault(dev.Formats) ?? dev.Formats.FirstOrDefault() ?? throw new InvalidOperationException("The device reports no formats");
                return () => CreateUvc(dev, fmt, api);
            }
            case "window":
                return () =>
                {
                    var w = Platform.WindowList.Find("", arg) ?? Platform.WindowList.Enumerate().FirstOrDefault(x => x.Title.Contains(arg, StringComparison.OrdinalIgnoreCase))
                        ?? throw new InvalidOperationException($"No window titled \"{arg}\"");
                    return new WindowSource(w.Hwnd, w.Title, w.Process);
                };
            default:
                throw new ArgumentException($"Unknown source \"{spec}\"");
        }
    }

    public static IVideoSource CreateUvc(CaptureDevice dev, CaptureFormat fmt, string api) =>
        api == "dshow" || (dev.DshowOnly && api != "mf") ? new DshowSource(dev.Name, fmt) : new UvcSource(dev.Id, dev.Name, fmt);

    /// <summary>Source from the saved settings.</summary>
    public static Func<IVideoSource> FromSettings(SourceSettings s, IReadOnlyList<CaptureDevice> devices)
    {
        switch (s.Kind)
        {
            case "uvc":
            {
                var dev = devices.FirstOrDefault(d => d.Id == s.DeviceId) ?? throw new InvalidOperationException("The capture card was not found. Plug it in, then press Refresh in Settings.");
                var fmt = FormatPicker.Find(dev.Formats, s.Width, s.Height, s.Fps, s.Subtype) ?? FormatPicker.PickDefault(dev.Formats)
                    ?? throw new InvalidOperationException("The capture card reports no usable format");
                return () => CreateUvc(dev, fmt, s.Api);
            }
            case "window":
                return () =>
                {
                    var w = Platform.WindowList.Find(s.Window.Process, s.Window.Title) ?? throw new InvalidOperationException($"The window \"{s.Window.Title}\" is not open");
                    return new WindowSource(w.Hwnd, w.Title, w.Process);
                };
            case "file":
                return () => new FileSource(s.File.Path, s.File.Container);
            case "raw":
                return () => new RawDumpSource(s.Raw.Path);
            default:
            {
                var o = PatternOptions.Parse(s.Pattern.Cadence) with { Layout = PatternOptions.ParseLayout(s.Pattern.Layout) };
                return () => new TestPatternSource(o);
            }
        }
    }
}
