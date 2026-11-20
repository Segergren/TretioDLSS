using System.Net.Http.Headers;
using System.Text.Json;
using Serilog;

namespace TretioDLSS.App;

public sealed record ReleaseNote(string Version, string Notes);

/// <summary>The notes of the releases between the version that ran last and this one, from GitHub.</summary>
public static class ReleaseNotes
{
    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        c.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue(AppInfo.Name, AppInfo.Version));
        c.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return c;
    }

    /// <summary>Newest first; null when GitHub could not be reached, so the caller tries again next start.</summary>
    public static async Task<List<ReleaseNote>?> FetchAsync(Version after, Version upTo)
    {
        try
        {
            string repo = new Uri(AppInfo.UpdateSource).AbsolutePath.Trim('/');
            using var doc = JsonDocument.Parse(await Http.GetStringAsync($"https://api.github.com/repos/{repo}/releases?per_page=30"));
            var list = new List<(Version V, ReleaseNote Note)>();
            foreach (var r in doc.RootElement.EnumerateArray())
            {
                if (r.GetProperty("draft").GetBoolean()) continue;
                string tag = r.GetProperty("tag_name").GetString() ?? "";
                if (!Version.TryParse(tag.TrimStart('v'), out var v) || v <= after || v > upTo) continue;
                string body = r.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "";
                if (body.Trim().Length == 0) continue;
                list.Add((v, new ReleaseNote(v.ToString(), body.Trim())));
            }
            return list.OrderByDescending(x => x.V).Select(x => x.Note).ToList();
        }
        catch (Exception ex)
        {
            Log.Information("Release notes not fetched: {Error}", ex.Message);
            return null;
        }
    }
}
