using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Web;
using System.Xml.Linq;
using Serilog;

namespace TretioDLSS.Streaming;

/// <summary>Launch request from a paired client: the stream keys and the mode it asked for.</summary>
public sealed record LaunchRequest(string ClientName, byte[] RiKey, uint RiKeyId, int Width, int Height, int Fps, bool HostAudio, string RemoteAddress, string LocalAddress);

/// <summary>
/// The GameStream HTTP (pairing, server info) and HTTPS (app list, launch) endpoints, on a small
/// hand-written HTTP/1.1 server so HTTPS can ask for the client's certificate without admin rights.
/// </summary>
public sealed class GameStreamHttp : IDisposable
{
    public const string AppVersion = "7.1.431.-1";
    public const string GfeVersion = "3.23.0.74";
    public const int AppId = 1;
    private const int ScmH264 = 0x1, ScmHevc = 0x100, ScmAv1Main8 = 0x10000;

    private readonly HostIdentity _id;
    private readonly GameStreamPorts _ports;
    private readonly string _hostName;
    private TcpListener? _http, _https;
    private readonly CancellationTokenSource _cts = new();

    public Func<LaunchRequest, (bool Ok, string Error)>? OnLaunch;
    public Action? OnCancel;
    public Func<bool>? IsStreaming;

    /// <summary>A client asks to pair: the UI must show a PIN prompt for this device name.</summary>
    public event Action<string>? PinRequested;
    public event Action<string, bool>? PairingFinished;

    private sealed class PairSession
    {
        public string DeviceName = "";
        public string ClientCertPem = "";
        public byte[] Salt = [];
        public byte[]? AesKey;
        public byte[] ServerSecret = [];
        public byte[] ServerChallenge = [];
        public byte[] ClientHash = [];
        public TaskCompletionSource<string?> Pin = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private readonly Dictionary<string, PairSession> _pairing = new();
    private string? _waitingPin;

    public GameStreamHttp(HostIdentity id, GameStreamPorts ports, string hostName)
    {
        _id = id;
        _ports = ports;
        _hostName = hostName;
    }

    public string? WaitingForPinFrom => _waitingPin;

    public void Start()
    {
        _http = new TcpListener(IPAddress.IPv6Any, _ports.Http);
        _http.Server.DualMode = true;
        _http.Start();
        _https = new TcpListener(IPAddress.IPv6Any, _ports.Https);
        _https.Server.DualMode = true;
        _https.Start();
        _ = AcceptLoop(_http, false);
        _ = AcceptLoop(_https, true);
        Log.Information("GameStream HTTP on {Http}, HTTPS on {Https}", _ports.Http, _ports.Https);
    }

    /// <summary>The PIN the user typed for the pending pairing; null cancels it.</summary>
    public void SubmitPin(string? pin)
    {
        lock (_pairing)
            foreach (var s in _pairing.Values) s.Pin.TrySetResult(pin);
    }

    private async Task AcceptLoop(TcpListener listener, bool tls)
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await listener.AcceptTcpClientAsync(_cts.Token); }
            catch { break; }
            _ = Task.Run(() => Handle(client, tls));
        }
    }

    private async Task Handle(TcpClient client, bool tls)
    {
        using var _ = client;
        client.NoDelay = true;
        var remote = (IPEndPoint)client.Client.RemoteEndPoint!;
        var local = (IPEndPoint)client.Client.LocalEndPoint!;
        System.IO.Stream stream = client.GetStream();
        X509Certificate2? clientCert = null;
        try
        {
            if (tls)
            {
                var ssl = new SslStream(stream, false, (_, _, _, _) => true);
                await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                {
                    ServerCertificate = _id.TlsCertificate,
                    ClientCertificateRequired = true,
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                    CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                }, _cts.Token);
                if (ssl.RemoteCertificate != null) clientCert = new X509Certificate2(ssl.RemoteCertificate);
                stream = ssl;
            }
            client.ReceiveTimeout = 10000;
            string? request = await ReadRequest(stream);
            if (request == null) return;
            var (path, query) = ParseRequestLine(request);
            Log.Debug("GameStream {Scheme} {Path} from {Remote}", tls ? "HTTPS" : "HTTP", path, remote.Address);
            if (path == "/appasset" && tls && _id.Find(clientCert) != null)
            {
                await RespondBytes(stream, AppCover.Bytes, "image/png");
                return;
            }
            XElement root = await Route(path, query, tls, clientCert, Normalize(remote.Address), Normalize(local.Address));
            await Respond(stream, root);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "GameStream request from {Remote} failed", remote.Address);
        }
    }

    private static string Normalize(IPAddress a) => (a.IsIPv4MappedToIPv6 ? a.MapToIPv4() : a).ToString();

    private static async Task<string?> ReadRequest(System.IO.Stream s)
    {
        var buf = new byte[16384];
        int n = 0;
        while (n < buf.Length)
        {
            int r = await s.ReadAsync(buf.AsMemory(n));
            if (r == 0) return n == 0 ? null : Encoding.ASCII.GetString(buf, 0, n);
            n += r;
            if (Encoding.ASCII.GetString(buf, 0, n).Contains("\r\n\r\n")) break;
        }
        return Encoding.ASCII.GetString(buf, 0, n);
    }

    private static (string Path, System.Collections.Specialized.NameValueCollection Query) ParseRequestLine(string request)
    {
        string line = request[..request.IndexOf("\r\n", StringComparison.Ordinal)];
        string target = line.Split(' ')[1];
        int q = target.IndexOf('?');
        string path = q < 0 ? target : target[..q];
        var query = HttpUtility.ParseQueryString(q < 0 ? "" : target[(q + 1)..]);
        return (path, query);
    }

    private static async Task Respond(System.IO.Stream s, XElement root)
    {
        string xml = "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n" + root.ToString(SaveOptions.DisableFormatting);
        byte[] body = Encoding.UTF8.GetBytes(xml);
        string head = $"HTTP/1.1 200 OK\r\nContent-Type: application/xml\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n";
        await s.WriteAsync(Encoding.ASCII.GetBytes(head));
        await s.WriteAsync(body);
        await s.FlushAsync();
    }

    private static async Task RespondBytes(System.IO.Stream s, byte[] body, string type)
    {
        string head = $"HTTP/1.1 200 OK\r\nContent-Type: {type}\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n";
        await s.WriteAsync(Encoding.ASCII.GetBytes(head));
        await s.WriteAsync(body);
        await s.FlushAsync();
    }

    private static XElement Status(int code, string? message = null)
    {
        var root = new XElement("root", new XAttribute("status_code", code));
        if (message != null) root.Add(new XAttribute("status_message", message));
        return root;
    }

    private async Task<XElement> Route(string path, System.Collections.Specialized.NameValueCollection q, bool tls, X509Certificate2? cert, string remote, string local)
    {
        PairedClient? paired = tls ? _id.Find(cert) : null;
        switch (path)
        {
            case "/serverinfo":
                if (tls && paired == null) return Status(401, "The client is not authorized. Certificate verification failed.");
                return ServerInfo(tls && paired != null, local);
            case "/pair":
                return await Pair(q, tls, paired);
            case "/unpair":
                return Status(200);
        }
        if (!tls || paired == null) return Status(401, "The client is not authorized. Certificate verification failed.");
        switch (path)
        {
            case "/applist":
                return new XElement("root", new XAttribute("status_code", 200),
                    new XElement("App", new XElement("IsHdrSupported", 0), new XElement("AppTitle", "TretioDLSS"), new XElement("ID", AppId)));
            case "/launch":
            case "/resume":
                return Launch(q, paired, remote, local);
            case "/cancel":
                OnCancel?.Invoke();
                return new XElement("root", new XAttribute("status_code", 200), new XElement("cancel", 1));
            default:
                return Status(404, "Not found");
        }
    }

    private XElement ServerInfo(bool paired, string local)
    {
        bool streaming = IsStreaming?.Invoke() == true;
        return new XElement("root", new XAttribute("status_code", 200),
            new XElement("hostname", _hostName),
            new XElement("appversion", AppVersion),
            new XElement("GfeVersion", GfeVersion),
            new XElement("uniqueid", _id.UniqueId),
            new XElement("HttpsPort", _ports.Https),
            new XElement("ExternalPort", _ports.Http),
            new XElement("MaxLumaPixelsHEVC", "1869449984"),
            new XElement("mac", "00:00:00:00:00:00"),
            new XElement("LocalIP", local.Contains(':') ? "127.0.0.1" : local),
            new XElement("ServerCodecModeSupport", ScmH264 | ScmHevc | ScmAv1Main8),
            new XElement("PairStatus", paired ? 1 : 0),
            new XElement("currentgame", streaming ? AppId : 0),
            new XElement("state", streaming ? "SUNSHINE_SERVER_BUSY" : "SUNSHINE_SERVER_FREE"));
    }

    private XElement Launch(System.Collections.Specialized.NameValueCollection q, PairedClient client, string remote, string local)
    {
        string? rikey = q["rikey"], rikeyid = q["rikeyid"];
        if (rikey == null || rikeyid == null) return Status(400, "Missing a required launch parameter");
        var mode = (q["mode"] ?? "1920x1080x60").Split('x');
        int w = int.TryParse(mode.ElementAtOrDefault(0), out var mw) ? mw : 1920;
        int h = int.TryParse(mode.ElementAtOrDefault(1), out var mh) ? mh : 1080;
        int fps = int.TryParse(mode.ElementAtOrDefault(2), out var mf) ? mf : 60;
        uint keyId = unchecked((uint)long.Parse(rikeyid));
        var req = new LaunchRequest(client.Name, Convert.FromHexString(rikey), keyId, w, h, fps, q["localAudioPlayMode"] == "1", remote, local);
        Log.Information("GameStream launch from {Client} ({Remote}): {W}x{H}@{Fps}, host audio {HostAudio}", client.Name, remote, w, h, fps, req.HostAudio);
        var (ok, error) = OnLaunch?.Invoke(req) ?? (false, "Not ready");
        if (!ok)
        {
            var fail = Status(503, error);
            fail.Add(new XElement("gamesession", 0));
            return fail;
        }
        string host = local.Contains(':') ? $"[{local}]" : local;
        return new XElement("root", new XAttribute("status_code", 200),
            new XElement("sessionUrl0", $"rtsp://{host}:{_ports.Rtsp}"),
            new XElement("gamesession", 1));
    }

    private async Task<XElement> Pair(System.Collections.Specialized.NameValueCollection q, bool tls, PairedClient? paired)
    {
        string? uniqueId = q["uniqueid"];
        if (uniqueId == null) return Status(400, "Missing uniqueid parameter");
        if (q["phrase"] == "pairchallenge")
        {
            var ok = Status(200);
            ok.Add(new XElement("paired", 1));
            return ok;
        }
        if (q["phrase"] == "getservercert") return await GetServerCert(uniqueId, q);

        PairSession? s;
        lock (_pairing) _pairing.TryGetValue(uniqueId, out s);
        if (s?.AesKey == null) return Status(400, "Invalid uniqueid");
        var root = new XElement("root", new XAttribute("status_code", 200));
        try
        {
            if (q["clientchallenge"] is string cc)
            {
                var challenge = AesEcb(s.AesKey, Convert.FromHexString(cc), false);
                s.ServerSecret = RandomNumberGenerator.GetBytes(16);
                s.ServerChallenge = RandomNumberGenerator.GetBytes(16);
                var hash = SHA256.HashData([.. challenge, .. _id.CertSignature, .. s.ServerSecret]);
                root.Add(new XElement("paired", 1), new XElement("challengeresponse", Convert.ToHexString(AesEcb(s.AesKey, [.. hash, .. s.ServerChallenge], true))));
            }
            else if (q["serverchallengeresp"] is string scr)
            {
                s.ClientHash = AesEcb(s.AesKey, Convert.FromHexString(scr), false);
                root.Add(new XElement("paired", 1), new XElement("pairingsecret", Convert.ToHexString([.. s.ServerSecret, .. _id.Sign(s.ServerSecret)])));
            }
            else if (q["clientpairingsecret"] is string cps)
            {
                var all = Convert.FromHexString(cps);
                var secret = all[..16];
                var sign = all[16..];
                using var clientCert = X509Certificate2.CreateFromPem(s.ClientCertPem);
                var expected = SHA256.HashData([.. s.ServerChallenge, .. HostIdentity.Signature(clientCert), .. secret]);
                bool hashOk = s.ClientHash.Length >= 32 && expected.AsSpan().SequenceEqual(s.ClientHash.AsSpan(0, 32));
                using var rsa = clientCert.GetRSAPublicKey();
                bool signOk = rsa?.VerifyData(secret, sign, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1) == true;
                bool ok = hashOk && signOk;
                if (ok) _id.AddClient(s.DeviceName, s.ClientCertPem);
                Log.Information("GameStream pairing with {Device}: {Result} (hash {Hash}, signature {Sign})", s.DeviceName, ok ? "paired" : "failed", hashOk, signOk);
                lock (_pairing) _pairing.Remove(uniqueId);
                PairingFinished?.Invoke(s.DeviceName, ok);
                root.Add(new XElement("paired", ok ? 1 : 0));
            }
            else
            {
                return Status(400, "Invalid pairing request");
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "GameStream pairing step failed");
            lock (_pairing) _pairing.Remove(uniqueId);
            PairingFinished?.Invoke(s.DeviceName, false);
            var fail = Status(400, "Pairing failed");
            fail.Add(new XElement("paired", 0));
            return fail;
        }
        return root;
    }

    private async Task<XElement> GetServerCert(string uniqueId, System.Collections.Specialized.NameValueCollection q)
    {
        var s = new PairSession
        {
            DeviceName = q["devicename"] ?? "Moonlight",
            ClientCertPem = Encoding.ASCII.GetString(Convert.FromHexString(q["clientcert"] ?? "")),
            Salt = Convert.FromHexString((q["salt"] ?? "").PadRight(32, '0')[..32]),
        };
        lock (_pairing)
        {
            foreach (var old in _pairing.Values) old.Pin.TrySetResult(null);
            _pairing[uniqueId] = s;
        }
        _waitingPin = s.DeviceName;
        Log.Information("GameStream: {Device} wants to pair, waiting for the PIN", s.DeviceName);
        PinRequested?.Invoke(s.DeviceName);
        string? pin;
        try { pin = await s.Pin.Task.WaitAsync(TimeSpan.FromMinutes(3)); }
        catch (TimeoutException) { pin = null; }
        _waitingPin = null;
        if (string.IsNullOrEmpty(pin))
        {
            lock (_pairing) _pairing.Remove(uniqueId);
            PairingFinished?.Invoke(s.DeviceName, false);
            var fail = Status(400, "Pairing cancelled");
            fail.Add(new XElement("paired", 0));
            return fail;
        }
        s.AesKey = SHA256.HashData([.. s.Salt, .. Encoding.UTF8.GetBytes(pin)])[..16];
        var root = new XElement("root", new XAttribute("status_code", 200));
        root.Add(new XElement("paired", 1), new XElement("plaincert", Convert.ToHexString(Encoding.ASCII.GetBytes(_id.CertPem))));
        return root;
    }

    private static byte[] AesEcb(byte[] key, byte[] data, bool encrypt)
    {
        using var aes = Aes.Create();
        aes.Key = key;
        if (data.Length % 16 != 0) Array.Resize(ref data, (data.Length + 15) / 16 * 16);
        return encrypt ? aes.EncryptEcb(data, PaddingMode.None) : aes.DecryptEcb(data, PaddingMode.None);
    }

    public void Dispose()
    {
        _cts.Cancel();
        SubmitPin(null);
        try { _http?.Stop(); } catch { }
        try { _https?.Stop(); } catch { }
    }
}

/// <summary>GameStream ports, all derived from one base like Sunshine's (HTTP is the base).</summary>
public sealed record GameStreamPorts(int Http)
{
    public int Https => Http - 5;
    public int Video => Http + 9;
    public int Control => Http + 10;
    public int Audio => Http + 11;
    public int Rtsp => Http + 21;
}
