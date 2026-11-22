namespace TretioDLSS.Core;

public sealed record ProfileInfo(string Name, string Summary);

/// <summary>Named setups. Every settings change is stored into the active profile, so the last one used comes back on start.</summary>
public static class Profiles
{
    public const string DefaultName = "Profile 1";

    public static ProfileData? Find(Settings s, string name) =>
        s.Profiles.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

    public static string Summary(ProfileData p)
    {
        var src = p.Source;
        string input = src.Kind == "uvc" ? $"{src.Height}p{Math.Round(src.Fps)}" : src.Kind;
        string fg = p.FrameGen.Engine switch
        {
            "off" => "no frame generation",
            "vfx-vfg" => $"NVIDIA FG x{p.FrameGen.Multiplier}",
            "dlss-fg" => $"DLSS FG x{p.FrameGen.Multiplier}",
            "rife" => $"RIFE x{p.FrameGen.Multiplier}",
            "amd-frc" => $"AMD FG x{p.FrameGen.Multiplier}",
            "blend" => $"Blend x{p.FrameGen.Multiplier}",
            var x => x,
        };
        string up = p.Upscale.Engine switch
        {
            "off" => "no upscaling",
            "vfx-vsr" or "rtx-video" => "RTX Video",
            "fsr1" => "FSR 1",
            "nis" => "NIS",
            var x => x,
        };
        string summary = $"{input}, {fg}, {up}";
        return p.Neural.Engine == "dlss5" ? $"{summary}, DLSS 5 with {p.Neural.Layers} layer{(p.Neural.Layers == 1 ? "" : "s")}" : summary;
    }

    public static IEnumerable<ProfileInfo> List(Settings s) => s.Profiles.Select(p => new ProfileInfo(p.Name, Summary(p)));

    /// <summary>Makes sure there is at least one profile and that the active one exists.</summary>
    public static void Ensure(Settings s)
    {
        if (s.Profiles.Count == 0) s.Profiles.Add(Capture(s, DefaultName));
        var active = Find(s, s.ActiveProfile) ?? s.Profiles[0];
        s.ActiveProfile = active.Name;
    }

    /// <summary>Writes the current setup into the active profile.</summary>
    public static void Store(Settings s)
    {
        Ensure(s);
        var p = Find(s, s.ActiveProfile)!;
        var snap = Capture(s, p.Name);
        p.Source = snap.Source;
        p.Cadence = snap.Cadence;
        p.Upscale = snap.Upscale;
        p.Neural = snap.Neural;
        p.FrameGen = snap.FrameGen;
        p.Display = snap.Display;
    }

    /// <summary>Loads the profile's setup into the settings and makes it active.</summary>
    public static void Apply(Settings s, ProfileData p)
    {
        s.Source = Clone(p.Source);
        s.Cadence = Clone(p.Cadence);
        s.Upscale = Clone(p.Upscale);
        s.Neural = Clone(p.Neural);
        s.FrameGen = Clone(p.FrameGen);
        s.Display = Clone(p.Display);
        s.ActiveProfile = p.Name;
    }

    public static ProfileData Capture(Settings s, string name) => new()
    {
        Name = name,
        Source = Clone(s.Source),
        Cadence = Clone(s.Cadence),
        Upscale = Clone(s.Upscale),
        Neural = Clone(s.Neural),
        FrameGen = Clone(s.FrameGen),
        Display = Clone(s.Display),
    };

    /// <summary>A name not taken yet: "Profile 2", "Profile 3", ...</summary>
    public static string NextName(Settings s)
    {
        for (int i = 2; ; i++)
        {
            string name = $"Profile {i}";
            if (Find(s, name) == null) return name;
        }
    }

    private static T Clone<T>(T value) =>
        System.Text.Json.JsonSerializer.Deserialize<T>(System.Text.Json.JsonSerializer.Serialize(value, Json.Options), Json.Options)!;
}
