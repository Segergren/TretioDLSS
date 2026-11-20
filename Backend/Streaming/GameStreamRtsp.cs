using System.Net;
using System.Net.Sockets;
using System.Text;
using Serilog;

namespace TretioDLSS.Streaming;

/// <summary>The RTSP handshake: one TCP connection per request, the server closes after answering.</summary>
public sealed class GameStreamRtsp : IDisposable
{
    private readonly GameStreamPorts _ports;
    private TcpListener? _listener;
    private readonly CancellationTokenSource _cts = new();

    public Func<StreamSession?>? Current;

    /// <summary>ANNOUNCE complete: the session has its format; returns false to refuse it.</summary>
    public Func<StreamSession, bool>? OnAnnounce;

    public GameStreamRtsp(GameStreamPorts ports) => _ports = ports;

    public void Start()
    {
        _listener = new TcpListener(IPAddress.IPv6Any, _ports.Rtsp);
        _listener.Server.DualMode = true;
        _listener.Start();
        _ = Accept();
    }

    private async Task Accept()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient c;
            try { c = await _listener!.AcceptTcpClientAsync(_cts.Token); }
            catch { break; }
            _ = Task.Run(() => Handle(c));
        }
    }

    private async Task Handle(TcpClient client)
    {
        using var _ = client;
        client.NoDelay = true;
        var stream = client.GetStream();
        try
        {
            var (head, body) = await Read(stream);
            if (head == null) return;
            var lines = head.Split("\r\n");
            var parts = lines[0].Split(' ');
            string method = parts[0], target = parts.Length > 1 ? parts[1] : "";
            var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var l in lines.Skip(1))
            {
                int i = l.IndexOf(':');
                if (i > 0) options[l[..i].Trim()] = l[(i + 1)..].Trim();
            }
            string seq = options.GetValueOrDefault("CSeq", "0");
            Log.Debug("RTSP {Method} {Target}", method, target);
            var session = Current?.Invoke();
            string response = session == null ? Reply(seq, 404, "NOT FOUND") : method switch
            {
                "OPTIONS" => Reply(seq, 200, "OK"),
                "DESCRIBE" => Reply(seq, 200, "OK", payload: Describe()),
                "SETUP" => Setup(seq, target, session),
                "ANNOUNCE" => Announce(seq, body, options, session),
                "PLAY" => Reply(seq, 200, "OK"),
                _ => Reply(seq, 404, "NOT FOUND"),
            };
            await stream.WriteAsync(Encoding.ASCII.GetBytes(response));
            await stream.FlushAsync();
            client.Client.Shutdown(SocketShutdown.Send);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "RTSP request failed");
        }
    }

    private static async Task<(string? Head, string Body)> Read(NetworkStream s)
    {
        var buf = new MemoryStream();
        var chunk = new byte[8192];
        int headEnd = -1;
        int contentLength = 0;
        while (true)
        {
            int n = await s.ReadAsync(chunk).AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            if (n == 0) break;
            buf.Write(chunk, 0, n);
            string text = Encoding.ASCII.GetString(buf.GetBuffer(), 0, (int)buf.Length);
            if (headEnd < 0)
            {
                headEnd = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                if (headEnd >= 0)
                {
                    foreach (var l in text[..headEnd].Split("\r\n"))
                        if (l.StartsWith("Content-length:", StringComparison.OrdinalIgnoreCase)) int.TryParse(l[15..].Trim(), out contentLength);
                }
            }
            if (headEnd >= 0 && buf.Length >= headEnd + 4 + contentLength) break;
        }
        if (headEnd < 0) return (null, "");
        string all = Encoding.ASCII.GetString(buf.GetBuffer(), 0, (int)buf.Length);
        return (all[..headEnd], all[(headEnd + 4)..]);
    }

    private static string Reply(string seq, int code, string status, IEnumerable<(string, string)>? extra = null, string payload = "")
    {
        var sb = new StringBuilder();
        sb.Append($"RTSP/1.0 {code} {status}\r\nCSeq: {seq}\r\n");
        if (extra != null) foreach (var (k, v) in extra) sb.Append($"{k}: {v}\r\n");
        sb.Append("\r\n").Append(payload);
        return sb.ToString();
    }

    private static string Describe()
    {
        var sb = new StringBuilder();
        sb.Append("a=x-ss-general.featureFlags:0\n");
        sb.Append($"a=x-ss-general.encryptionSupported:{StreamSession.EncControlV2 | StreamSession.EncAudio}\n");
        sb.Append($"a=x-ss-general.encryptionRequested:{StreamSession.EncControlV2}\n");
        sb.Append("sprop-parameter-sets=AAAAAU\n");
        sb.Append("a=rtpmap:98 AV1/90000\n");
        sb.Append("a=fmtp:97 surround-params=21101\n");
        return sb.ToString();
    }

    private string Setup(string seq, string target, StreamSession session)
    {
        int eq = target.IndexOf('=');
        string type = eq < 0 ? "" : target[(eq + 1)..].Split('/')[0];
        int port = type switch { "audio" => _ports.Audio, "video" => _ports.Video, "control" => _ports.Control, _ => 0 };
        if (port == 0) return Reply(seq, 404, "NOT FOUND");
        var extra = new List<(string, string)>
        {
            ("Session", "DEADBEEFCAFE;timeout = 90"),
            ("Transport", $"server_port={port}"),
            type == "control" ? ("X-SS-Connect-Data", session.ConnectData.ToString()) : ("X-SS-Ping-Payload", session.PingPayload),
        };
        return Reply(seq, 200, "OK", extra);
    }

    private string Announce(string seq, string body, Dictionary<string, string> options, StreamSession session)
    {
        var a = new Dictionary<string, string>();
        foreach (var raw in body.Split('\n'))
        {
            string line = raw.TrimEnd('\r', ' ');
            if (!line.StartsWith("a=")) continue;
            int i = line.IndexOf(':');
            if (i < 0) continue;
            a.TryAdd(line[2..i], line[(i + 1)..].Trim());
        }
        int Int(string key, int fallback) => a.TryGetValue(key, out var v) && long.TryParse(v, out var n) ? (int)n : fallback;

        session.PacketSize = Int("x-nv-video[0].packetSize", 1392);
        session.MinFecPackets = Int("x-nv-vqos[0].fec.minRequiredFecPackets", 0);
        session.AudioChannels = Int("x-nv-audio.surround.numChannels", 2);
        session.AudioPacketMs = Int("x-nv-aqos.packetDuration", 5);
        session.EncryptionFlags = Int("x-ss-general.encryptionEnabled", 0);
        session.MlFeatureFlags = Int("x-ml-general.featureFlags", 0);
        if ((Int("x-nv-general.featureFlags", 135) & 0x20) != 0) session.EncryptionFlags |= StreamSession.EncAudio;
        if (session.AudioChannels == 2) session.AudioHighQuality = !options.GetValueOrDefault("Host", "").Contains("0.0.0.0");

        int width = Int("x-nv-video[0].clientViewportWd", session.Launch.Width);
        int height = Int("x-nv-video[0].clientViewportHt", session.Launch.Height);
        int fps = Int("x-nv-video[0].maxFPS", session.Launch.Fps);
        int codec = Int("x-nv-vqos[0].bitStreamFormat", 0);
        int kbps = Int("x-nv-vqos[0].bw.maximumBitrateKbps", 20000);
        int configured = Int("x-ml-video.configuredBitrateKbps", 0);
        if (configured > 0)
        {
            double k = configured * 0.8;
            k -= Math.Min((session.AudioHighQuality ? 256 : 96) * session.AudioChannels, k / 5);
            k -= Math.Min(500, k / 10);
            kbps = (int)k;
        }
        session.Format = new StreamFormat(width, height, fps, codec, Math.Max(1000, kbps));
        Log.Information("GameStream ANNOUNCE: {W}x{H}@{Fps}, codec {Codec}, {Kbps} kbps, packet {Packet}, min FEC {Fec}, audio {Ch} ch {Ms} ms {Hq}, encryption {Enc}",
            width, height, fps, codec switch { 1 => "HEVC", 2 => "AV1", _ => "H.264" }, kbps, session.PacketSize, session.MinFecPackets, session.AudioChannels, session.AudioPacketMs,
            session.AudioHighQuality ? "high quality" : "normal", session.EncryptionFlags);
        if (session.AudioChannels != 2) Log.Warning("GameStream: the client asked for {Ch} audio channels; only stereo is sent", session.AudioChannels);
        bool ok = OnAnnounce?.Invoke(session) ?? false;
        return ok ? Reply(seq, 200, "OK") : Reply(seq, 500, "Internal Server Error");
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _listener?.Stop(); } catch { }
    }
}
