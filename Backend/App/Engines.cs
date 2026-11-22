using TretioDLSS.Engines;

namespace TretioDLSS.App;

public sealed record EngineOptionValue(string Value, string Label);
public sealed record EngineOption(string Key, string Label, List<EngineOptionValue> Values);

public sealed record EngineInfo(string Id, string Name, bool Available, string? Reason = null, string? FixHint = null, int[]? Multipliers = null, List<EngineOption>? Options = null, InstallState? Install = null);

/// <summary>Which engines exist on this machine, and why the others do not.</summary>
public static class EngineCatalog
{
    public static string Name(string id) => id switch
    {
        "vfx-vsr" or "rtx-video" => "RTX Video",
        "vfx-vfg" => "NVIDIA Frame Generation",
        "dlss-fg" => "DLSS Frame Generation",
        NeuralRuntime.Id => "DLSS 5",
        "rife" => "RIFE",
        "amd-frc" => "AMD Frame Generation",
        "fsr1" => "FSR 1",
        "nis" => "NVIDIA Image Scaling",
        "blend" => "Blend",
        "bilinear" => "Bilinear",
        "bicubic" => "Bicubic",
        _ => id,
    };

    /// <summary>An engine the last run died in stays unavailable for this session.</summary>
    private static List<EngineInfo> WithSafeMode(List<EngineInfo> list) =>
        list.Select(e => SafeMode.IsDisabled(e.Id) ? e with { Available = false, Reason = SafeMode.Message(e.Id), FixHint = SafeMode.Message(e.Id) } : e).ToList();

    public static List<EngineInfo> Upscalers(Capabilities caps)
    {
        var list = new List<EngineInfo>
        {
            new("off", "Off", true),
            new("bilinear", "Bilinear", true),
            new("bicubic", "Bicubic", true),
            new("fsr1", "FSR 1", true),
            new("nis", "NVIDIA Image Scaling", true),
        };
        if (!caps.Nvidia)
            list.Add(new("vfx-vsr", "RTX Video", false, "Needs an NVIDIA RTX GPU", "Needs an NVIDIA RTX GPU"));
        else if (!VfxRuntime.HasFeature("vfx-vsr"))
            list.Add(new("vfx-vsr", "RTX Video", false, "Not downloaded", "Download it from NVIDIA", Install: VfxInstaller.State("vfx-vsr")));
        else
            list.Add(new("vfx-vsr", "RTX Video", true, Options:
            [
                new("quality", "Quality", [new("low", "Low"), new("medium", "Medium"), new("high", "High"), new("ultra", "Ultra")]),
            ]));
        return WithSafeMode(list);
    }

    /// <summary>The DLSS 5 stage: Off, or DLSS 5 with 1 to 4 layers (sent as the multipliers).</summary>
    public static List<EngineInfo> Neural(Capabilities caps)
    {
        int[] layers = [1, 2, 3, 4];
        var list = new List<EngineInfo> { new("off", "Off", true) };
        if (!caps.Nvidia)
            list.Add(new(NeuralRuntime.Id, "DLSS 5", false, "Needs an NVIDIA RTX GPU", "Needs an NVIDIA RTX GPU", layers));
        else if (!caps.Ada)
            list.Add(new(NeuralRuntime.Id, "DLSS 5", false, "Needs an RTX 40 or 50 series GPU", "Needs an RTX 40 or 50 series GPU", layers));
        else if (!NeuralRuntime.DriverOk(caps.Driver))
            list.Add(new(NeuralRuntime.Id, "DLSS 5", false, $"Needs NVIDIA driver {NeuralRuntime.MinDriver:0.00} or newer", $"Update the NVIDIA driver to {NeuralRuntime.MinDriver:0.00} or newer", layers));
        else if (NeuralRuntime.BridgeMissing() is string missing)
            list.Add(new(NeuralRuntime.Id, "DLSS 5", false, "Not installed", missing, layers));
        else if (!NeuralRuntime.IsInstalledFor(caps.Arch))
            list.Add(new(NeuralRuntime.Id, "DLSS 5", false, "Not downloaded", "Download it", layers, Install: NeuralRuntime.State(caps.Arch)));
        else
            list.Add(new(NeuralRuntime.Id, "DLSS 5", true, Multipliers: layers));
        return WithSafeMode(list);
    }

    public static List<EngineInfo> FrameGenerators(Capabilities caps)
    {
        var list = new List<EngineInfo> { new("off", "Off", true) };

        var vfxModels = new List<EngineOption>
        {
            new("model", "Model", [new("low", "Low"), new("medium", "Medium"), new("high", "High")]),
        };
        if (!caps.Nvidia || !caps.Ada)
            list.Add(new("vfx-vfg", "NVIDIA Frame Generation", false, "Needs an RTX 40 or 50 series GPU", "Needs an RTX 40 or 50 series GPU", FrameGen.Multipliers("vfx-vfg"), vfxModels));
        else if (!VfxRuntime.HasFeature("vfx-vfg"))
            list.Add(new("vfx-vfg", "NVIDIA Frame Generation", false, "Not downloaded", "Download it from NVIDIA", FrameGen.Multipliers("vfx-vfg"), vfxModels, VfxInstaller.State("vfx-vfg")));
        else
            list.Add(new("vfx-vfg", "NVIDIA Frame Generation", true, Multipliers: FrameGen.Multipliers("vfx-vfg"), Options: vfxModels));

        if (!caps.Nvidia || !caps.Ada)
            list.Add(new("dlss-fg", "DLSS Frame Generation", false, "Needs an RTX 40 or 50 series GPU", "Needs an RTX 40 or 50 series GPU", [2, 3, 4]));
        else if (DlssFrameGenerator.Missing() is string missing)
            list.Add(new("dlss-fg", "DLSS Frame Generation", false, "Not installed", missing, [2, 3, 4]));
        else
            list.Add(new("dlss-fg", "DLSS Frame Generation", true, Multipliers: [2, 3, 4]));

        var models = RifeFrameGenerator.Models().OrderByDescending(m => m).Select(m => new EngineOptionValue(m, RifeFrameGenerator.ModelLabel(m))).ToList();
        if (models.Count == 0)
            list.Add(new("rife", "RIFE", false, "No RIFE models", "Build them with tools/rife/export.py", [2, 3, 4]));
        else
            list.Add(new("rife", "RIFE", caps.RifeReady, caps.RifeReady ? null : "ONNX Runtime with DirectML did not load", null, [2, 3, 4], [new("model", "Model", models)]));
        var amdSearch = new List<EngineOption> { new("model", "Motion search", [new("native", "Full resolution"), new("performance", "Fast, for integrated GPUs")]) };
        if (caps.AmfVersion == null)
            list.Add(new("amd-frc", "AMD Frame Generation", false, "Needs an AMD Radeon GPU and driver", "Needs an AMD Radeon GPU and driver", [2], amdSearch));
        else if (!caps.AmdPipeline)
            list.Add(new("amd-frc", "AMD Frame Generation", false, "The picture runs on a non-AMD GPU", "Runs only when the picture is on the AMD GPU", [2], amdSearch));
        else
            list.Add(new("amd-frc", "AMD Frame Generation", true, Multipliers: [2], Options: amdSearch));
        return WithSafeMode(list);
    }
}
