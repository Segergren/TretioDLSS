using System.Net;
using System.Net.Sockets;
using Serilog;
using TretioDLSS.Graphics;
using TretioDLSS.Output;

namespace TretioDLSS.Streaming;

/// <summary>
/// The built-in GameStream host: Moonlight on a TV pairs with it, launches "TretioDLSS", and gets the
/// pipeline's output encoded with NVENC. No Sunshine, no virtual display.
/// </summary>
public sealed class GameStreamHost : IDisposable
{
    public const string HostName = "TretioDLSS";
    private readonly HostIdentity _id = new();
    private GameStreamHttp? _http;
    private GameStreamRtsp? _rtsp;
    private StreamTransport? _transport;
    private IntPtr _mdns;
    private StreamSession? _session;
    private StreamSink? _sink;
    private AudioStreamer? _audio;

    public GameStreamPorts? Ports { get; private set; }
    public bool Running => _http != null;
    public string? Error { get; private set; }
    public StreamSession? Session => _session;
    public bool Streaming => _session?.Format != null;
    public string? WaitingForPinFrom => _http?.WaitingForPinFrom;
    public IReadOnlyList<PairedClient> Clients => _id.Clients;
    public StreamTransport? Transport => _transport;

    /// <summary>ANNOUNCE arrived: start the pipeline with a sink made by CreateSink.</summary>
    public Action<StreamSession>? StartPipeline;
    public Action? StopPipeline;
    public event Action? Changed;

    public void Start()
    {
        if (Running) return;
        Error = null;
        if (!StreamNative.Available)
        {
            Error = "TretioStream.dll is missing next to the app";
            Log.Warning("GameStream host not started: {Error}", Error);
            return;
        }
        try
        {
            _id.Load();
            Ports = new GameStreamPorts(PickBasePort());
            _transport = new StreamTransport(Ports);
            _transport.Start();
            _transport.Disconnected += reason => Task.Run(() => EndSession(reason, false));
            _transport.IdrRequested += () => _sink?.Encoder.RequestIdr();
            _http = new GameStreamHttp(_id, Ports, HostName)
            {
                OnLaunch = Launch,
                OnCancel = () => Task.Run(() => EndSession("Moonlight quit the app", true)),
                IsStreaming = () => _session != null,
            };
            _http.PinRequested += _ => Changed?.Invoke();
            _http.PairingFinished += (_, _) => Changed?.Invoke();
            _http.Start();
            _rtsp = new GameStreamRtsp(Ports) { Current = () => _session, OnAnnounce = Announce };
            _rtsp.Start();
            RegisterMdns();
            Log.Information("GameStream host ready as \"{Name}\" on port {Port}, {Clients} paired client(s)", HostName, Ports.Http, _id.Clients.Count);
        }
        catch (Exception ex)
        {
            Error = ex.Message;
            Log.Error(ex, "GameStream host failed to start");
            Stop();
        }
        Changed?.Invoke();
    }

    /// <summary>Sunshine's default ports when they are free; otherwise a block 200 lower so both can run.</summary>
    private static int PickBasePort()
    {
        foreach (int b in new[] { 47989, 47789, 47589 })
        {
            var p = new GameStreamPorts(b);
            if (TcpFree(p.Http) && TcpFree(p.Https) && TcpFree(p.Rtsp) && UdpFree(p.Video) && UdpFree(p.Audio) && UdpFree(p.Control)) return b;
        }
        throw new InvalidOperationException("No free GameStream ports (47989, 47789, 47589 are all in use)");
    }

    private static bool TcpFree(int port)
    {
        try
        {
            var l = new TcpListener(IPAddress.Any, port);
            l.Start();
            l.Stop();
            return true;
        }
        catch { return false; }
    }

    private static bool UdpFree(int port)
    {
        try
        {
            using var u = new UdpClient(port);
            return true;
        }
        catch { return false; }
    }

    private unsafe void RegisterMdns()
    {
        var err = stackalloc byte[256];
        string instance = $"{HostName}._nvstream._tcp.local";
        string host = Dns.GetHostName() + ".local";
        _mdns = StreamNative.ts_mdns_register(instance, host, (ushort)Ports!.Http, err, 256);
        string e = StreamNative.Text(err);
        if (_mdns == IntPtr.Zero || e.Length > 0) Log.Warning("GameStream mDNS: {Error}", e.Length > 0 ? e : "registration failed");
        else Log.Information("GameStream mDNS: {Instance} on {Host}:{Port}", instance, host, Ports.Http);
    }

    public void SubmitPin(string? pin) => _http?.SubmitPin(pin);

    public void Unpair(string name)
    {
        _id.RemoveClient(name);
        Changed?.Invoke();
    }

    private (bool, string) Launch(LaunchRequest req)
    {
        if (_session != null) EndSession("A new launch replaced the session", true);
        var s = new StreamSession { Launch = req };
        _session = s;
        _transport!.Begin(s);
        Changed?.Invoke();
        return (true, "");
    }

    private bool Announce(StreamSession s)
    {
        if (s != _session || s.Format == null) return false;
        _audio = new AudioStreamer(s.AudioPacketMs, s.AudioHighQuality, packet => _transport?.SendAudio(packet.Span));
        Task.Run(() =>
        {
            try { StartPipeline?.Invoke(s); }
            catch (Exception ex) { Log.Error(ex, "Stream pipeline did not start"); }
            Changed?.Invoke();
        });
        return true;
    }

    /// <summary>The pipeline's output for the current session. Runs on the pipeline thread.</summary>
    public OutputSink CreateSink(GraphicsDevice g)
    {
        var s = _session ?? throw new InvalidOperationException("No stream session");
        var sink = new StreamSink(g, s.Format!, frame => _transport?.SendVideo(frame));
        _sink = sink;
        return sink;
    }

    /// <summary>Card audio for the stream: interleaved stereo float.</summary>
    public void OnAudio(ReadOnlySpan<float> interleaved, int sampleRate) => _audio?.Write(interleaved, sampleRate);

    private readonly object _endGate = new();

    public void EndSession(string reason, bool notifyClient)
    {
        lock (_endGate)
        {
            if (_session == null) return;
            Log.Information("GameStream session ended: {Reason} (video {Frames} frames, {Packets} packets; audio {Audio} packets)",
                reason, _transport?.VideoFrames, _transport?.VideoPackets, _transport?.AudioPackets);
            _session = null;
            _audio = null;
            _transport?.End(notifyClient);
            StopPipeline?.Invoke();
            _sink = null;
        }
        Changed?.Invoke();
    }

    public void Stop()
    {
        EndSession("The host stopped", true);
        if (_mdns != IntPtr.Zero) StreamNative.ts_mdns_deregister(_mdns);
        _mdns = IntPtr.Zero;
        _rtsp?.Dispose();
        _http?.Dispose();
        _transport?.Dispose();
        _rtsp = null;
        _http = null;
        _transport = null;
        Changed?.Invoke();
    }

    public void Dispose() => Stop();
}
