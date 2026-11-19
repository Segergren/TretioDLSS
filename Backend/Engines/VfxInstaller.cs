using System.IO.Compression;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Serilog;
using TretioDLSS.App;

namespace TretioDLSS.Engines;

public sealed record InstallState(string State, double Progress, long Bytes, string? Error);

/// <summary>
/// Installs Video Effects features from NVIDIA's public nvidia-vfx wheel. Only the
/// needed DLLs are read out of it with HTTP range requests, then checked against the wheel's RECORD hashes.
/// </summary>
public static class VfxInstaller
{
    public const string WheelUrl = "https://pypi.nvidia.com/nvidia-vfx/nvidia_vfx-0.2.0.0-cp312-abi3-win_amd64.whl";

    private sealed record Part(string Name, string Sha256, long Size);

    private static readonly Part[] Core =
    [
        new("cudart64_12.dll", "Zjtf5lqmxlS4sIWoefSd6xnyywM7jy_43_0Gze_g0ds", 584304),
        new("NVCVImage.dll", "aJpkWGyARGgSdke37SSsw2qal4BQL_6VrRGqT-Moajk", 2694256),
        new("NVVideoEffects.dll", "xrBC_Fe9tr6ojJxSy5UVgjd9PxZDrPUqUtGXGVoeqPQ", 104560),
    ];

    private static readonly Dictionary<string, (string Selector, Part[] Parts)> Features = new()
    {
        ["vfx-vfg"] = ("VideoFrameGeneration",
        [
            new("nvVFXVideoFrameGeneration.dll", "Jwz0__kymQj5dw0wbLUbkQ7gQyOvVSDNAqmKLiKrp5U", 207321712),
        ]),
        ["vfx-vsr"] = ("VideoSuperRes",
        [
            new("nvngxruntime.dll", "0tvRmTAAou7Sx62M1Bf_xzjqGAAD2qzxkOc3Ehhp1jc", 83568),
            new("nvVFXVideoSuperRes.dll", "k2ARdZSdD1tcAz1snljKRlpF7x3ZYerqNNT_uXn3KFA", 42608),
            new("nvngx_vsr.dll", "DMmGQ6dsBcptc-yBqJjVRbGOXAgGZ9coVKG_rr1W4e0", 59120752),
        ]),
    };

    private static readonly Dictionary<string, InstallState> _states = new();
    private static readonly object _gate = new();

    /// <summary>Where downloads go; --install --to sets it, for example for the installer build.</summary>
    public static string? TargetOverride { get; set; }

    /// <summary>%LOCALAPPDATA%\TretioDLSS\nvvfx, or inside TRETIO_DATA for test runs.</summary>
    public static string InstallDir => TargetOverride ?? (
        string.IsNullOrEmpty(Environment.GetEnvironmentVariable("TRETIO_DATA"))
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppInfo.Name, "nvvfx")
            : Path.Combine(AppInfo.DataFolder, "nvvfx"));

    public static bool Knows(string id) => Features.ContainsKey(id);

    private static IEnumerable<Part> PartsFor(string id) => Core.Concat(Features[id].Parts);

    public static IEnumerable<string> Ids => Features.Keys;

    public static bool IsInstalledIn(string dir, string id) =>
        Features.ContainsKey(id) && PartsFor(id).All(p => File.Exists(Path.Combine(dir, p.Name)));

    public static bool IsInstalled(string id) => IsInstalledIn(InstallDir, id);

    /// <summary>Bytes still to download for this feature, so the button can show the size.</summary>
    public static long MissingBytes(string id) =>
        PartsFor(id).Where(p => !File.Exists(Path.Combine(InstallDir, p.Name))).Sum(p => p.Size);

    public static InstallState State(string id)
    {
        lock (_gate)
        {
            if (_states.TryGetValue(id, out var s) && s.State != "done") return s;
        }
        return VfxRuntime.HasFeature(id) ? new("installed", 1, 0, null) : new("none", 0, MissingBytes(id), null);
    }

    public static bool Busy
    {
        get { lock (_gate) return _states.Values.Any(s => s.State == "downloading"); }
    }

    /// <summary>Downloads, verifies and test-loads one feature. Returns null on success, else the reason.</summary>
    public static async Task<string?> InstallAsync(string id, Action changed, CancellationToken ct = default)
    {
        if (!Features.TryGetValue(id, out var feature)) return "Unknown component";
        lock (_gate)
        {
            if (_states.Values.Any(s => s.State == "downloading")) return "Another download is running";
            _states[id] = new("downloading", 0, MissingBytes(id), null);
        }
        changed();
        try
        {
            Directory.CreateDirectory(InstallDir);
            var missing = PartsFor(id).Where(p => !File.Exists(Path.Combine(InstallDir, p.Name))).ToList();
            long total = missing.Sum(p => p.Size), done = 0;
            Log.Information("Installing {Id}: {Count} files, {Mb:F0} MB, from {Url}", id, missing.Count, total / 1e6, WheelUrl);
            var lastReport = DateTime.MinValue;

            if (missing.Count > 0)
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
                http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue(AppInfo.Name, AppInfo.Version));
                using var head = await http.SendAsync(new HttpRequestMessage(HttpMethod.Head, WheelUrl), ct);
                head.EnsureSuccessStatusCode();
                long size = head.Content.Headers.ContentLength ?? throw new IOException("The download server sent no file size");
                using var remote = new HttpRangeStream(http, WheelUrl, size, ct);
                using var zip = new ZipArchive(remote, ZipArchiveMode.Read);
                foreach (var part in missing)
                {
                    var entry = zip.GetEntry("nvvfx/libs/" + part.Name) ?? throw new IOException($"{part.Name} is missing from NVIDIA's package");
                    string target = Path.Combine(InstallDir, part.Name), temp = target + ".part";
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
                                    lock (_gate) _states[id] = new("downloading", total == 0 ? 1 : (double)done / total, total, null);
                                    changed();
                                }
                            }
                        }
                        string hash = Convert.ToBase64String(sha.GetHashAndReset()).TrimEnd('=').Replace('+', '-').Replace('/', '_');
                        if (hash != part.Sha256)
                        {
                            File.Delete(temp);
                            throw new IOException($"{part.Name} failed its checksum");
                        }
                    }
                    File.Move(temp, target, overwrite: true);
                    Log.Information("Installed {File} ({Mb:F1} MB)", part.Name, part.Size / 1e6);
                }
            }

            // Packaging may run without an NVIDIA GPU; the hashes already prove the files.
            string? check = TargetOverride == null ? TestLoad(feature.Selector) : null;
            if (check != null) throw new IOException(check);
            Log.Information("{Id} installed in {Dir}; load check {Check}", id, InstallDir, TargetOverride == null ? "passed" : "skipped for packaging");
            lock (_gate) _states[id] = new("done", 1, 0, null);
            return null;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Installing {Id} failed", id);
            string reason = ex is OperationCanceledException ? "Download canceled" : ex is HttpRequestException ? "Download failed: " + ex.Message : ex.Message;
            lock (_gate) _states[id] = new("failed", 0, MissingBytes(id), reason);
            return reason;
        }
        finally
        {
            changed();
        }
    }

    [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetDllDirectoryW(string? path);

    private delegate int CreateEffectFn([MarshalAs(UnmanagedType.LPStr)] string selector, out IntPtr handle);
    private delegate void DestroyEffectFn(IntPtr handle);

    /// <summary>Creates and destroys the effect once, which proves the runtime and the feature load together.</summary>
    public static string? TestLoad(string selector)
    {
        try
        {
            SetDllDirectoryW(InstallDir);
            var lib = NativeLibrary.Load(Path.Combine(InstallDir, "NVVideoEffects.dll"));
            var create = Marshal.GetDelegateForFunctionPointer<CreateEffectFn>(NativeLibrary.GetExport(lib, "NvVFX_CreateEffect"));
            var destroy = Marshal.GetDelegateForFunctionPointer<DestroyEffectFn>(NativeLibrary.GetExport(lib, "NvVFX_DestroyEffect"));
            int status = create(selector, out var handle);
            if (status != 0 || handle == IntPtr.Zero) return $"NVIDIA's runtime installed, but the {selector} effect did not load (status {status})";
            destroy(handle);
            return null;
        }
        catch (Exception ex)
        {
            return "NVIDIA's runtime did not load: " + ex.Message;
        }
        finally
        {
            SetDllDirectoryW(null);
        }
    }
}

/// <summary>A read-only, seekable view of a remote file, read in large ranges.</summary>
internal sealed class HttpRangeStream(HttpClient http, string url, long length, CancellationToken ct) : Stream
{
    private const int Chunk = 8 << 20;
    private long _pos;
    private byte[] _buf = [];
    private long _bufStart = -1;

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => length;
    public override long Position { get => _pos; set => _pos = value; }

    public override int Read(byte[] buffer, int offset, int count)
    {
        if (_pos >= length) return 0;
        if (_bufStart < 0 || _pos < _bufStart || _pos >= _bufStart + _buf.Length) Fill(_pos);
        int n = (int)Math.Min(count, _bufStart + _buf.Length - _pos);
        Array.Copy(_buf, _pos - _bufStart, buffer, offset, n);
        _pos += n;
        return n;
    }

    private void Fill(long start)
    {
        long end = Math.Min(length, start + Chunk) - 1;
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.Range = new RangeHeaderValue(start, end);
                using var resp = http.Send(req, ct);
                if (resp.StatusCode != System.Net.HttpStatusCode.PartialContent)
                    throw new HttpRequestException($"the server answered {(int)resp.StatusCode} to a range request");
                using var ms = new MemoryStream((int)(end - start + 1));
                resp.Content.ReadAsStream(ct).CopyTo(ms);
                _buf = ms.ToArray();
                _bufStart = start;
                return;
            }
            catch (Exception) when (attempt < 3 && !ct.IsCancellationRequested)
            {
                Thread.Sleep(1000 * attempt);
            }
        }
    }

    public override long Seek(long offset, SeekOrigin origin) => _pos = origin switch
    {
        SeekOrigin.Begin => offset,
        SeekOrigin.Current => _pos + offset,
        _ => length + offset,
    };

    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
