namespace TretioDLSS.Core;

public sealed class Settings
{
    public int SchemaVersion { get; set; } = 1;
    public SourceSettings Source { get; set; } = new();
    public CadenceSettings Cadence { get; set; } = new();
    public UpscaleSettings Upscale { get; set; } = new();
    public FrameGenSettings FrameGen { get; set; } = new();
    public DisplaySettings Display { get; set; } = new();
    public TvSettings Tv { get; set; } = new();
    public AudioSettings Audio { get; set; } = new();
    public HotkeySettings Hotkeys { get; set; } = new();
    public AdvancedSettings Advanced { get; set; } = new();
    public UiSettings Ui { get; set; } = new();
    public DiscordSettings Discord { get; set; } = new();
    public List<ProfileData> Profiles { get; set; } = new();
    public string ActiveProfile { get; set; } = Core.Profiles.DefaultName;
}

public sealed class SourceSettings
{
    public string Kind { get; set; } = "uvc";
    public string Api { get; set; } = "auto";
    public string DeviceId { get; set; } = "";
    public int Width { get; set; } = 1920;
    public int Height { get; set; } = 1080;
    public double Fps { get; set; } = 60;
    public string Subtype { get; set; } = "NV12";
    public string ColorMatrix { get; set; } = "auto";
    public string ColorRange { get; set; } = "auto";
    public WindowRef Window { get; set; } = new();
    public FileRef File { get; set; } = new();
    public RawRef Raw { get; set; } = new();
    public PatternRef Pattern { get; set; } = new();
}

public sealed class WindowRef
{
    public string Process { get; set; } = "";
    public string Title { get; set; } = "";
}

public sealed class FileRef
{
    public string Path { get; set; } = "";
    public int Container { get; set; } = 60;
}

public sealed class RawRef
{
    public string Path { get; set; } = "";
}

public sealed class PatternRef
{
    public string Cadence { get; set; } = "30in60";
    public string Layout { get; set; } = "nv12";
}

public sealed class CadenceSettings
{
    public string Mode { get; set; } = "auto";
    public double FixedFps { get; set; } = 30;
}

public sealed class UpscaleSettings
{
    public string Engine { get; set; } = "vfx-vsr";
    public string Size { get; set; } = "display";
    public double Sharpness { get; set; } = 0.2;
    public Dictionary<string, string> Options { get; set; } = new();
}

public sealed class FrameGenSettings
{
    public string Engine { get; set; } = "vfx-vfg";
    public int Multiplier { get; set; } = 2;
    public string Model { get; set; } = "medium";
    public string Version { get; set; } = "";
    public string StageOrder { get; set; } = "auto";
    public Dictionary<string, string> Options { get; set; } = new();
}

public sealed class DisplaySettings
{
    public string MonitorId { get; set; } = "";
    public string Mode { get; set; } = "borderless";
    public string Sync { get; set; } = "auto";
    public string Pacing { get; set; } = "auto";
    public bool Overlay { get; set; }
    public string WindowTitle { get; set; } = "TretioDLSS output";
    public HdrSettings Hdr { get; set; } = new();
}

/// <summary>SDR to HDR output when Windows HDR is on for the display.</summary>
public sealed class HdrSettings
{
    /// <summary>"auto" uses HDR when the display is in HDR mode; "off" always outputs SDR.</summary>
    public string Mode { get; set; } = "auto";
    public double PaperWhite { get; set; } = 200;
    public double Expand { get; set; } = 0.35;
    public double Brightness { get; set; }
    public double Contrast { get; set; } = 1;
    public double Saturation { get; set; } = 1;
}

public sealed class TvSettings
{
    public string Mode { get; set; } = "off";
}

public sealed class AudioSettings
{
    public string InputId { get; set; } = "auto";
    public string OutputId { get; set; } = "default";
    public double Volume { get; set; } = 1.0;
    public int OffsetMs { get; set; }
    public bool AutoSync { get; set; } = true;
}

public sealed class HotkeySettings
{
    public string ToggleRun { get; set; } = "Ctrl+Alt+F10";
    public string ToggleFrameGen { get; set; } = "Ctrl+Alt+F9";
    public string ToggleOverlay { get; set; } = "Ctrl+Alt+F8";
    public string CompareUpscaler { get; set; } = "Ctrl+Alt+F7";
    public string ExitFullscreen { get; set; } = "Ctrl+Alt+F11";
}

/// <summary>Discord activity for the console game while playback runs.</summary>
public sealed class DiscordSettings
{
    public bool Enabled { get; set; }
    public string Game { get; set; } = "Grand Theft Auto VI";
    /// <summary>"HH:mm" for a fixed start time; empty starts the timer with playback.</summary>
    public string StartTime { get; set; } = "";
}

public sealed class UiSettings
{
    /// <summary>The close button hides the window to the tray instead of exiting.</summary>
    public bool CloseToTray { get; set; }

    /// <summary>The version that ran last; a newer one shows its release notes once.</summary>
    public string LastVersion { get; set; } = "";
}

public sealed class AdvancedSettings
{
    public string Adapter { get; set; } = "auto";
    public string LogLevel { get; set; } = "info";
    public string VfxRuntimePath { get; set; } = "";
}

/// <summary>A named setup: source and format, processing and output. Audio, hotkeys and app options stay global.</summary>
public sealed class ProfileData
{
    public string Name { get; set; } = "";
    public SourceSettings Source { get; set; } = new();
    public CadenceSettings Cadence { get; set; } = new();
    public UpscaleSettings Upscale { get; set; } = new();
    public FrameGenSettings FrameGen { get; set; } = new();
    public DisplaySettings Display { get; set; } = new();
}
