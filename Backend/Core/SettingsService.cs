using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Serilog;
using TretioDLSS.App;

namespace TretioDLSS.Core;

public static class Json
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DictionaryKeyPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        NumberHandling = JsonNumberHandling.AllowReadingFromString | JsonNumberHandling.AllowNamedFloatingPointLiterals,
        PropertyNameCaseInsensitive = true,
    };

    public static readonly JsonSerializerOptions Compact = new(Options) { WriteIndented = false };
}

public sealed class SettingsService
{
    public const int CurrentSchema = 1;

    private readonly string _path;
    private readonly object _gate = new();
    private Timer? _saveTimer;

    public Settings Current { get; private set; } = new();
    public event Action<Settings>? Changed;

    public SettingsService(string path)
    {
        _path = path;
    }

    /// <summary>Returns false when no settings file existed (first run).</summary>
    public bool Load()
    {
        lock (_gate)
        {
            if (!File.Exists(_path))
            {
                Current = new Settings();
                Profiles.Ensure(Current);
                return false;
            }
            try
            {
                var node = JsonNode.Parse(File.ReadAllText(_path)) as JsonObject ?? new JsonObject();
                Migrate(node);
                Current = node.Deserialize<Settings>(Json.Options) ?? new Settings();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Settings file unreadable, starting from defaults");
                try { File.Copy(_path, _path + ".broken", true); } catch { }
                Current = new Settings();
            }
            Normalize(Current);
            return true;
        }
    }

    /// <summary>Upgrades older settings files in place. Each step bumps schemaVersion by one.</summary>
    public static void Migrate(JsonObject node)
    {
        int version = node["schemaVersion"]?.GetValue<int>() ?? 0;
        if (version < 1)
        {
            // Version 0 had no schema field; nothing to move yet.
            version = 1;
        }
        node["schemaVersion"] = version;
    }

    private static void Normalize(Settings s)
    {
        s.SchemaVersion = CurrentSchema;
        s.Upscale.Sharpness = Math.Clamp(s.Upscale.Sharpness, 0, 1);
        s.Audio.Volume = Math.Clamp(s.Audio.Volume, 0, 1);
        s.Audio.OffsetMs = Math.Clamp(s.Audio.OffsetMs, -200, 300);
        s.FrameGen.Multiplier = Math.Clamp(s.FrameGen.Multiplier, 1, 8);
        var h = s.Display.Hdr;
        h.PaperWhite = Math.Clamp(h.PaperWhite, 80, 500);
        h.Expand = Math.Clamp(h.Expand, 0, 1);
        h.Brightness = Math.Clamp(h.Brightness, -0.2, 0.2);
        h.Contrast = Math.Clamp(h.Contrast, 0.8, 1.2);
        h.Saturation = Math.Clamp(h.Saturation, 0.8, 1.4);
        Profiles.Store(s);
    }

    public void Save()
    {
        lock (_gate)
        {
            _saveTimer?.Dispose();
            _saveTimer = null;
            Profiles.Store(Current);
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(Current, Json.Options));
            File.Move(tmp, _path, true);
        }
    }

    public void SaveSoon()
    {
        lock (_gate)
        {
            _saveTimer?.Dispose();
            _saveTimer = new Timer(_ => { try { Save(); } catch (Exception ex) { Log.Error(ex, "Saving settings failed"); } }, null, 500, Timeout.Infinite);
        }
    }

    /// <summary>Deep-merges a partial settings object from the frontend.</summary>
    public void ApplyPatch(JsonElement patch)
    {
        lock (_gate)
        {
            var node = JsonSerializer.SerializeToNode(Current, Json.Options)!.AsObject();
            var patchNode = JsonNode.Parse(patch.GetRawText()) as JsonObject;
            if (patchNode == null) return;
            MergeInto(node, patchNode);
            Current = node.Deserialize<Settings>(Json.Options) ?? Current;
            Normalize(Current);
        }
        SaveSoon();
        Changed?.Invoke(Current);
    }

    public void Mutate(Action<Settings> change)
    {
        lock (_gate)
        {
            change(Current);
            Normalize(Current);
        }
        SaveSoon();
        Changed?.Invoke(Current);
    }

    internal static void MergeInto(JsonObject target, JsonObject patch)
    {
        foreach (var (key, value) in patch)
        {
            var existingKey = target.Select(kv => kv.Key).FirstOrDefault(k => string.Equals(k, key, StringComparison.OrdinalIgnoreCase)) ?? key;
            if (value is JsonObject childPatch && target[existingKey] is JsonObject childTarget)
                MergeInto(childTarget, childPatch);
            else
                target[existingKey] = value?.DeepClone();
        }
    }
}
