namespace TretioDLSS.Core;

public sealed record ProfileInfo(string Name, bool BuiltIn, string Summary);

public static class Profiles
{
    public static readonly ProfileData[] BuiltIns =
    [
        new()
        {
            Name = "30 to 60",
            Upscale = new UpscaleSettings { Engine = "vfx-vsr", Sharpness = 0.2 },
            FrameGen = new FrameGenSettings { Engine = "vfx-vfg", Multiplier = 2, Model = "medium" },
            Pacing = "auto",
        },
        new()
        {
            Name = "30 to 120",
            Upscale = new UpscaleSettings { Engine = "vfx-vsr", Sharpness = 0.2 },
            FrameGen = new FrameGenSettings { Engine = "vfx-vfg", Multiplier = 4, Model = "medium" },
            Pacing = "auto",
        },
        new()
        {
            Name = "Lowest delay",
            Upscale = new UpscaleSettings { Engine = "fsr1", Sharpness = 0.25 },
            FrameGen = new FrameGenSettings { Engine = "off", Multiplier = 1 },
            Pacing = "immediate",
        },
    ];

    public static IEnumerable<ProfileData> All(Settings s) => BuiltIns.Concat(s.Profiles);

    public static ProfileData? Find(Settings s, string name) =>
        All(s).FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

    public static string Summary(ProfileData p)
    {
        string fg = p.FrameGen.Engine switch
        {
            "off" => "No frame generation",
            "vfx-vfg" => $"NVIDIA Frame Generation x{p.FrameGen.Multiplier}",
            "blend" => $"Blend x{p.FrameGen.Multiplier}",
            "rife" => $"RIFE x{p.FrameGen.Multiplier}",
            _ => p.FrameGen.Engine,
        };
        string up = p.Upscale.Engine switch
        {
            "off" => "no upscaling",
            "vfx-vsr" or "rtx-video" => "RTX Video",
            "fsr1" => "FSR 1",
            "nis" => "NIS",
            var x => x,
        };
        return $"{fg}, {up}";
    }

    public static IEnumerable<ProfileInfo> List(Settings s) =>
        All(s).Select(p => new ProfileInfo(p.Name, BuiltIns.Contains(p), Summary(p)));

    /// <summary>Copies the profile's processing choices onto the settings. Hardware choices stay.</summary>
    public static void Apply(Settings s, ProfileData p)
    {
        s.Cadence = Clone(p.Cadence);
        s.Upscale = Clone(p.Upscale);
        s.FrameGen = Clone(p.FrameGen);
        s.Display.Sync = p.Sync;
        s.Display.Pacing = p.Pacing;
        s.ActiveProfile = p.Name;
    }

    public static ProfileData Capture(Settings s, string name) => new()
    {
        Name = name,
        Cadence = Clone(s.Cadence),
        Upscale = Clone(s.Upscale),
        FrameGen = Clone(s.FrameGen),
        Sync = s.Display.Sync,
        Pacing = s.Display.Pacing,
    };

    private static T Clone<T>(T value) =>
        System.Text.Json.JsonSerializer.Deserialize<T>(System.Text.Json.JsonSerializer.Serialize(value, Json.Options), Json.Options)!;
}
