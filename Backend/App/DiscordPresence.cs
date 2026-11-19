using System.Net.Http;
using System.Text.Json;
using DiscordRPC;
using Serilog;
using TretioDLSS.Core;

namespace TretioDLSS.App;

/// <summary>
/// Shows the console game as a Discord activity while playback runs. The game name is matched against
/// Discord's list of detectable games, and the presence is sent under that game's own application id,
/// so Discord shows the real game with its icon.
/// </summary>
public sealed class DiscordPresence : IDisposable
{
    private const string DetectableUrl = "https://discord.com/api/v10/applications/detectable";
    private static readonly TimeSpan CacheAge = TimeSpan.FromDays(3);
    private static string CacheFile => Path.Combine(AppInfo.DataFolder, "discord-games.json");

    private readonly object _gate = new();
    private DiscordRpcClient? _client;
    private string _appId = "";
    private string _wanted = "";
    private string _start = "";
    private DateTime? _startUtc;
    private Dictionary<string, (string Id, string Name)>? _games;
    private List<GameEntry>? _list;

    public sealed record GameEntry(string Id, string Name, string Icon, string Key);

    private string _status = "";

    public event Action? StatusChanged;

    public string Status
    {
        get => _status;
        private set
        {
            if (value == _status) return;
            _status = value;
            StatusChanged?.Invoke();
        }
    }

    public void Update(DiscordSettings s, bool running)
    {
        string wanted = s.Enabled && running ? s.Game.Trim() : "";
        string start = s.StartTime.Trim();
        lock (_gate)
        {
            if (wanted == _wanted && start == _start) return;
            if (wanted != _wanted || _startUtc == null) _startUtc = DateTime.UtcNow;
            _wanted = wanted;
            _start = start;
        }
        Task.Run(() => Apply(wanted));
    }

    private async Task Apply(string wanted)
    {
        try
        {
            if (wanted.Length == 0)
            {
                Close();
                Status = "";
                return;
            }
            var game = await Find(wanted);
            lock (_gate)
            {
                if (wanted != _wanted) return;
                if (game == null)
                {
                    Close();
                    Status = $"Discord does not know a game called \"{wanted}\"";
                    Log.Warning("Discord: no detectable game matches {Game}", wanted);
                    return;
                }
                if (_client == null || _appId != game.Value.Id)
                {
                    Close();
                    _appId = game.Value.Id;
                    _client = new DiscordRpcClient(_appId);
                    _client.OnError += (_, e) => Log.Warning("Discord: {Message}", e.Message);
                    _client.Initialize();
                }
                _client.SetPresence(new RichPresence { Timestamps = new Timestamps(StartUtc()) });
                Status = $"Showing {game.Value.Name}";
                Log.Information("Discord: showing {Game} (application {Id})", game.Value.Name, game.Value.Id);
            }
        }
        catch (Exception ex)
        {
            Status = "Discord status failed: " + ex.Message;
            Log.Warning(ex, "Discord presence failed");
        }
    }

    /// <summary>Games from Discord's list whose name matches the query, best first.</summary>
    public async Task<List<GameEntry>> Search(string query, int max = 30)
    {
        if (_games == null) (_games, _list) = await Load();
        var key = Normalize(query);
        var words = query.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (key.Length == 0) return [];
        return _list!
            .Select(g => (g, rank: g.Key == key ? 0 : g.Key.StartsWith(key, StringComparison.Ordinal) ? 1 : g.Key.Contains(key, StringComparison.Ordinal) ? 2
                : words.All(w => g.Name.Contains(w, StringComparison.OrdinalIgnoreCase)) ? 3 : 9))
            .Where(x => x.rank < 9)
            .OrderBy(x => x.rank).ThenBy(x => x.g.Icon.Length == 0).ThenBy(x => x.g.Name.Length)
            .Take(max).Select(x => x.g).ToList();
    }

    private async Task<(string Id, string Name)?> Find(string name)
    {
        if (_games == null) (_games, _list) = await Load();
        var key = Normalize(name);
        if (_games.TryGetValue(key, out var hit)) return hit;
        var partial = _games.Where(g => g.Key.StartsWith(key, StringComparison.Ordinal)).OrderBy(g => g.Key.Length).FirstOrDefault();
        return partial.Value.Id == null ? null : partial.Value;
    }

    private static async Task<(Dictionary<string, (string, string)>, List<GameEntry>)> Load()
    {
        string json;
        var info = new FileInfo(CacheFile);
        if (info.Exists && DateTime.UtcNow - info.LastWriteTimeUtc < CacheAge)
        {
            json = await File.ReadAllTextAsync(CacheFile);
        }
        else
        {
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
                http.DefaultRequestHeaders.UserAgent.ParseAdd(AppInfo.Name + "/" + AppInfo.Version);
                json = await http.GetStringAsync(DetectableUrl);
                await File.WriteAllTextAsync(CacheFile, json);
            }
            catch when (info.Exists)
            {
                json = await File.ReadAllTextAsync(CacheFile);
            }
        }
        var map = new Dictionary<string, (string, string)>();
        var list = new List<GameEntry>();
        using var doc = JsonDocument.Parse(json);
        foreach (var app in doc.RootElement.EnumerateArray())
        {
            if (!app.TryGetProperty("id", out var id) || !app.TryGetProperty("name", out var n)) continue;
            var entry = (id.GetString()!, n.GetString()!);
            map.TryAdd(Normalize(entry.Item2), entry);
            string icon = app.TryGetProperty("icon_hash", out var ih) && ih.GetString() is string h && h.Length > 0
                ? $"https://cdn.discordapp.com/app-icons/{entry.Item1}/{h}.png?size=64" : "";
            list.Add(new GameEntry(entry.Item1, entry.Item2, icon, Normalize(entry.Item2)));
            if (app.TryGetProperty("aliases", out var aliases) && aliases.ValueKind == JsonValueKind.Array)
                foreach (var a in aliases.EnumerateArray())
                    if (a.GetString() is string alias) map.TryAdd(Normalize(alias), entry);
        }
        return (map, list);
    }

    /// <summary>The fixed start time today (yesterday if it lies ahead), else when playback started.</summary>
    private DateTime StartUtc()
    {
        if (TimeOnly.TryParse(_start, out var t))
        {
            var local = DateTime.Today.Add(t.ToTimeSpan());
            if (local > DateTime.Now) local = local.AddDays(-1);
            return local.ToUniversalTime();
        }
        return _startUtc ?? DateTime.UtcNow;
    }

    private static string Normalize(string s) =>
        new string(s.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

    private void Close()
    {
        lock (_gate)
        {
            if (_client == null) return;
            try { _client.ClearPresence(); } catch { }
            _client.Dispose();
            _client = null;
            _appId = "";
        }
    }

    public void Dispose() => Close();
}
