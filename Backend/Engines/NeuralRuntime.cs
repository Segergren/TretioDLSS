using System.Globalization;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using Serilog;
using TretioDLSS.App;

namespace TretioDLSS.Engines;

/// <summary>
/// The DLSS 5 runtime, nvngx_dlssnr.dll 310.9.16. NVIDIA's own build carries kernels for RTX 50 only; the
/// community rebuild of the same version adds RTX 20, 30 and 40 kernels and lowers the architecture check.
/// Neither is in NVIDIA's SDK, so both come from the public mirror the DLSS 5 community tools use.
/// </summary>
public static class NeuralRuntime
{
    public const string Id = "dlss5";
    public const string FileName = "nvngx_dlssnr.dll";
    public const string Caller = "TretioNr.nvngx.dll";
    public const double MinDriver = 616.56;
    private const string Mirror = "https://github.com/RankFTW/rhi-repo/releases/download/";

    public sealed record Build(string Name, string Url, string Sha256, long Size, long DownloadSize);

    public static readonly Build Nvidia = new("310.9.16 (NVIDIA)", Mirror + "dlssnr-310.9.16-nvidia/nvngx_dlssnr_310.9.16-nvidia.zip",
        "2BC19BC09A7B89DD44F73E2AC0108FCC586E2154BDD0830F242F4F040E28789F", 266_424_944, 184_678_049);

    public static readonly Build Community = new("310.9.16 (RTX 20 to 50 kernels)", Mirror + "dlssnr-310.9.16/nvngx_dlssnr_310.9.16.zip",
        "CDFA6C1EC784867182164C7836F324F087B5C9F374D0C9FE46F54999FEABF3ED", 276_162_560, 192_378_839);

    /// <summary>RTX 50 (Blackwell, 0x1B0) runs NVIDIA's build; older cards need the rebuild.</summary>
    public static Build For(uint? arch) => arch is uint a && a >= 0x1B0 ? Nvidia : Community;

    /// <summary>Where downloads go; --install --to sets it.</summary>
    public static string? TargetOverride { get; set; }

    /// <summary>%LOCALAPPDATA%\TretioDLSS\dlss5, or inside TRETIO_DATA for test runs.</summary>
    public static string InstallDir => TargetOverride ?? (
        string.IsNullOrEmpty(Environment.GetEnvironmentVariable("TRETIO_DATA"))
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppInfo.Name, Id)
            : Path.Combine(AppInfo.DataFolder, Id));

    public static string RuntimePath => Path.Combine(InstallDir, FileName);
    public static string CallerPath => Path.Combine(AppContext.BaseDirectory, Caller);

    /// <summary>Which known build sits in the install folder, by size; null when none.</summary>
    public static Build? Installed()
    {
        var f = new FileInfo(RuntimePath);
        if (!f.Exists) return null;
        return f.Length == Nvidia.Size ? Nvidia : f.Length == Community.Size ? Community : null;
    }

    /// <summary>The installed build runs on this GPU: the rebuild runs everywhere, NVIDIA's only on RTX 50.</summary>
    public static bool IsInstalledFor(uint? arch)
    {
        var b = Installed();
        return b == Community || b != null && b == For(arch);
    }

    /// <summary>Why the native bridge cannot run DLSS 5 from this install, or null.</summary>
    public static string? BridgeMissing()
    {
        string dir = AppContext.BaseDirectory;
        if (!File.Exists(Path.Combine(dir, DlssFrameGenerator.Bridge))) return $"{DlssFrameGenerator.Bridge} is missing; run Native/TretioNgx/build.ps1";
        if (!File.Exists(CallerPath)) return $"{Caller} is missing; run Native/TretioNgx/build.ps1";
        return null;
    }

    /// <summary>An unknown driver version passes; the runtime then says itself what it misses.</summary>
    public static bool DriverOk(string driver) =>
        !double.TryParse(driver, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) || v >= MinDriver;

    private static InstallState _state = new("none", 0, 0, null);
    private static readonly object _gate = new();

    public static InstallState State(uint? arch)
    {
        lock (_gate)
        {
            if (_state.State is "downloading" or "failed") return _state;
        }
        return IsInstalledFor(arch) ? new("installed", 1, 0, null) : new("none", 0, For(arch).DownloadSize, null, "GitHub");
    }

    /// <summary>Downloads and checks the build for this GPU. Returns null on success, else the reason.</summary>
    public static async Task<string?> InstallAsync(uint? arch, Action changed, CancellationToken ct = default)
    {
        var build = For(arch);
        lock (_gate)
        {
            if (_state.State == "downloading") return "Another download is running";
            _state = new("downloading", 0, build.DownloadSize, null, "GitHub");
        }
        changed();
        string temp = RuntimePath + ".part";
        try
        {
            Directory.CreateDirectory(InstallDir);
            Log.Information("Installing DLSS 5 {Build}: {Mb:F0} MB from {Url}", build.Name, build.DownloadSize / 1e6, build.Url);
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue(AppInfo.Name, AppInfo.Version));
            using var head = await http.SendAsync(new HttpRequestMessage(HttpMethod.Head, build.Url), ct);
            head.EnsureSuccessStatusCode();
            long size = head.Content.Headers.ContentLength ?? throw new IOException("The download server sent no file size");
            using var remote = new HttpRangeStream(http, build.Url, size, ct);
            using var zip = new ZipArchive(remote, ZipArchiveMode.Read);
            var entry = zip.GetEntry(FileName) ?? throw new IOException($"{FileName} is missing from the download");
            var lastReport = DateTime.MinValue;
            long done = 0;
            using (var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                await using (var src = entry.Open())
                await using (var dst = File.Create(temp))
                {
                    var buf = new byte[1 << 20];
                    int n;
                    while ((n = await src.ReadAsync(buf, ct)) > 0)
                    {
                        await dst.WriteAsync(buf.AsMemory(0, n), ct);
                        sha.AppendData(buf, 0, n);
                        done += n;
                        if (DateTime.UtcNow - lastReport > TimeSpan.FromMilliseconds(250))
                        {
                            lastReport = DateTime.UtcNow;
                            lock (_gate) _state = new("downloading", Math.Min(1, (double)done / build.Size), build.DownloadSize, null, "GitHub");
                            changed();
                        }
                    }
                }
                string hash = Convert.ToHexString(sha.GetHashAndReset());
                if (!hash.Equals(build.Sha256, StringComparison.OrdinalIgnoreCase)) throw new IOException($"{FileName} failed its checksum");
            }
            File.Move(temp, RuntimePath, overwrite: true);
            Log.Information("DLSS 5 {Build} installed in {Dir}", build.Name, InstallDir);
            lock (_gate) _state = new("none", 0, 0, null);
            return null;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Installing DLSS 5 failed");
            try { File.Delete(temp); } catch { }
            string reason = ex is OperationCanceledException ? "Download canceled" : ex is HttpRequestException ? "Download failed: " + ex.Message : ex.Message;
            lock (_gate) _state = new("failed", 0, build.DownloadSize, reason, "GitHub");
            return reason;
        }
        finally
        {
            changed();
        }
    }
}
