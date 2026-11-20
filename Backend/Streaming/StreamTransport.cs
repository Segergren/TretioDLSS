using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Serilog;

namespace TretioDLSS.Streaming;

/// <summary>
/// The live part of a session: the ENet control channel, and the UDP video and audio streams.
/// Packet layouts follow Sunshine's stream.cpp, so any Moonlight client understands them.
/// </summary>
public sealed unsafe class StreamTransport : IDisposable
{
    private const int MaxRtpHeader = 16, VideoHeader = 32, FecPercent = 20, MaxFecBlocks = 4;
    private const int AudioData = 4, AudioParity = 2;
    private static readonly byte[] AudioParityMatrix = [0x77, 0x40, 0x38, 0x0e, 0xc7, 0xa7, 0x0d, 0x6c];

    private readonly GameStreamPorts _ports;
    private Socket? _video, _audio;
    private IntPtr _enet;
    private Thread? _controlThread, _videoRecv, _audioRecv;
    private volatile bool _stop;
    private readonly object _sendGate = new();

    private StreamSession? _session;
    private IntPtr _peer;
    private EndPoint? _videoPeer, _audioPeer;
    private AesGcm? _controlCipher;
    private uint _controlSeq;
    private ushort _videoSeq;
    private readonly long _epochQpc = Pipeline.Qpc.Now;

    private IntPtr _audioRs;
    private readonly byte[][] _audioShards = new byte[AudioData + AudioParity][];
    private ushort _audioSeq;
    private uint _audioTimestamp;
    private Aes? _audioAes;

    public event Action? IdrRequested;
    public event Action<string>? Disconnected;
    public event Action? ControlConnected;

    public long VideoFrames, VideoPackets, AudioPackets;

    public StreamTransport(GameStreamPorts ports) => _ports = ports;

    public bool HasVideoPeer => _videoPeer != null;

    public void Start()
    {
        StreamNative.EnsureInit();
        _video = Bind(_ports.Video);
        _audio = Bind(_ports.Audio);
        _enet = StreamNative.ts_enet_host_create((ushort)_ports.Control, 4);
        if (_enet == IntPtr.Zero) throw new InvalidOperationException($"Control port {_ports.Control} is in use");
        _audioRs = StreamNative.ts_rs_new(AudioData, AudioParity);
        fixed (byte* p = AudioParityMatrix) StreamNative.ts_rs_set_parity(_audioRs, p, AudioParityMatrix.Length);
        for (int i = 0; i < _audioShards.Length; i++) _audioShards[i] = new byte[2048 + 16];
        _controlThread = new Thread(ControlLoop) { IsBackground = true, Name = "StreamControl", Priority = ThreadPriority.Highest };
        _videoRecv = new Thread(() => PingLoop(_video, true)) { IsBackground = true, Name = "StreamVideoPing" };
        _audioRecv = new Thread(() => PingLoop(_audio, false)) { IsBackground = true, Name = "StreamAudioPing" };
        _controlThread.Start();
        _videoRecv.Start();
        _audioRecv.Start();
    }

    private static Socket Bind(int port)
    {
        var s = new Socket(AddressFamily.InterNetworkV6, SocketType.Dgram, ProtocolType.Udp) { DualMode = true };
        s.SendBufferSize = 4 * 1024 * 1024;
        s.Bind(new IPEndPoint(IPAddress.IPv6Any, port));
        return s;
    }

    /// <summary>A new session replaces the old one; its peers are learned from the pings that follow.</summary>
    public void Begin(StreamSession session)
    {
        lock (_sendGate)
        {
            _session = session;
            _videoPeer = _audioPeer = null;
            _videoSeq = 0;
            _audioSeq = 0;
            _audioTimestamp = 0;
            _controlSeq = 0;
            _controlCipher?.Dispose();
            _controlCipher = new AesGcm(session.Launch.RiKey, 16);
            _audioAes?.Dispose();
            _audioAes = Aes.Create();
            _audioAes.Key = session.Launch.RiKey;
            VideoFrames = VideoPackets = AudioPackets = 0;
        }
    }

    public void End(bool sendTermination)
    {
        if (sendTermination && _peer != IntPtr.Zero)
        {
            var payload = new byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(payload, 0x80030023);
            SendControl(0x0109, payload);
            Thread.Sleep(50);
        }
        lock (_sendGate)
        {
            if (_peer != IntPtr.Zero) StreamNative.ts_enet_disconnect(_enet, _peer);
            _peer = IntPtr.Zero;
            _session = null;
            _videoPeer = _audioPeer = null;
        }
    }

    // ---- pings ----

    private void PingLoop(Socket? socket, bool video)
    {
        var buf = new byte[1500];
        while (!_stop && socket != null)
        {
            EndPoint from = new IPEndPoint(IPAddress.IPv6Any, 0);
            int n;
            try { n = socket.ReceiveFrom(buf, ref from); }
            catch { if (_stop) break; continue; }
            var s = _session;
            if (s == null) continue;
            string text = Encoding.ASCII.GetString(buf, 0, n);
            bool match = text.Contains(s.PingPayload) || text == "PING";
            if (!match) continue;
            lock (_sendGate)
            {
                if (video)
                {
                    if (_videoPeer == null) Log.Information("GameStream: video ping from {Peer}", from);
                    if (_videoPeer == null) IdrRequested?.Invoke();
                    _videoPeer = from;
                }
                else
                {
                    if (_audioPeer == null) Log.Information("GameStream: audio ping from {Peer}", from);
                    _audioPeer = from;
                }
            }
        }
    }

    // ---- control ----

    private void ControlLoop()
    {
        var buf = new byte[65536];
        while (!_stop)
        {
            int type;
            IntPtr peer;
            uint data;
            int len;
            lock (_sendGate)
                fixed (byte* p = buf) type = StreamNative.ts_enet_service(_enet, 0, out peer, out data, p, buf.Length, out len);
            if (type <= 0)
            {
                CheckTimeout();
                Thread.Sleep(2);
                continue;
            }
            var s = _session;
            switch (type)
            {
                case 1:
                    if (s == null || ((s.MlFeatureFlags & 0x02) != 0 && data != s.ConnectData))
                    {
                        Log.Warning("GameStream: control connection without a matching session (connect data {Data})", data);
                        lock (_sendGate) StreamNative.ts_enet_disconnect(_enet, peer);
                        break;
                    }
                    _peer = peer;
                    s.LastControl = DateTime.UtcNow;
                    Log.Information("GameStream: control channel connected");
                    ControlConnected?.Invoke();
                    break;
                case 2:
                    if (peer == _peer)
                    {
                        _peer = IntPtr.Zero;
                        Log.Information("GameStream: client disconnected");
                        Disconnected?.Invoke("The client disconnected");
                    }
                    break;
                case 3:
                    if (s != null && peer == _peer)
                    {
                        s.LastControl = DateTime.UtcNow;
                        OnControl(buf.AsSpan(0, len), s);
                    }
                    break;
            }
        }
    }

    private void CheckTimeout()
    {
        var s = _session;
        if (s == null || _peer == IntPtr.Zero) return;
        if (DateTime.UtcNow - s.LastControl > TimeSpan.FromSeconds(10))
        {
            Log.Warning("GameStream: no control traffic for 10 s, ending the session");
            _peer = IntPtr.Zero;
            Disconnected?.Invoke("The client stopped answering");
        }
    }

    private void OnControl(ReadOnlySpan<byte> packet, StreamSession s)
    {
        if (packet.Length < 2) return;
        ushort type = BinaryPrimitives.ReadUInt16LittleEndian(packet);
        if (type != 0x0001)
        {
            Log.Debug("GameStream: unencrypted control message {Type:X4} dropped", type);
            return;
        }
        if (packet.Length < 8 + 16 + 4) return;
        ushort length = BinaryPrimitives.ReadUInt16LittleEndian(packet[2..]);
        uint seq = BinaryPrimitives.ReadUInt32LittleEndian(packet[4..]);
        int cipherLen = length - 4 - 16;
        if (cipherLen < 4 || 8 + 16 + cipherLen > packet.Length) return;
        var tag = packet.Slice(8, 16);
        var cipher = packet.Slice(24, cipherLen);
        if (!s.ControlV2)
        {
            Log.Warning("GameStream: the client uses the old control encryption, which is not supported; update Moonlight");
            return;
        }
        Span<byte> iv = stackalloc byte[12];
        iv.Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(iv, seq);
        iv[10] = (byte)'C';
        iv[11] = (byte)'C';
        var plain = new byte[cipherLen];
        try
        {
            _controlCipher!.Decrypt(iv, cipher, tag, plain);
        }
        catch (CryptographicException)
        {
            Log.Warning("GameStream: control message failed to decrypt (seq {Seq})", seq);
            return;
        }
        ushort inner = BinaryPrimitives.ReadUInt16LittleEndian(plain);
        switch (inner)
        {
            case 0x0302:
                Log.Debug("GameStream: IDR requested");
                IdrRequested?.Invoke();
                break;
            case 0x0301:
                Log.Debug("GameStream: reference frames invalidated, sending an IDR");
                IdrRequested?.Invoke();
                break;
            case 0x0305:
            case 0x0307:
                Log.Debug("GameStream: start message {Type:X4}", inner);
                break;
        }
    }

    private void SendControl(ushort type, ReadOnlySpan<byte> payload)
    {
        var s = _session;
        if (s == null || _peer == IntPtr.Zero || _controlCipher == null) return;
        var plain = new byte[4 + payload.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(plain, type);
        BinaryPrimitives.WriteUInt16LittleEndian(plain.AsSpan(2), (ushort)payload.Length);
        payload.CopyTo(plain.AsSpan(4));
        uint seq = _controlSeq++;
        Span<byte> iv = stackalloc byte[12];
        iv.Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(iv, seq);
        iv[10] = (byte)'H';
        iv[11] = (byte)'C';
        var packet = new byte[8 + 16 + plain.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(packet, 0x0001);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(2), (ushort)(4 + 16 + plain.Length));
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4), seq);
        _controlCipher.Encrypt(iv, plain, packet.AsSpan(24), packet.AsSpan(8, 16));
        lock (_sendGate)
            fixed (byte* p = packet) StreamNative.ts_enet_send(_enet, _peer, 0, p, packet.Length, 1);
    }

    // ---- video ----

    /// <summary>Packetizes one encoded frame with FEC and sends it. Called from the encoder's worker thread.</summary>
    public void SendVideo(EncodedFrame frame)
    {
        var s = _session;
        var peer = _videoPeer;
        if (s == null || peer == null || _video == null) return;

        int packetSize = s.PacketSize;
        int blockSize = packetSize + MaxRtpHeader;
        int payloadBlock = blockSize - VideoHeader;
        var data = frame.Data;

        // Short frame header (8 bytes) in front of the bitstream.
        var header = new byte[8];
        header[0] = 0x01;
        long latency = Math.Clamp((long)(Pipeline.Qpc.ToMs(Pipeline.Qpc.Now - frame.CaptureQpc) * 10), 0, ushort.MaxValue);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(1), (ushort)latency);
        header[3] = (byte)(frame.Idr ? 2 : 1);
        int last = (data.Length + 8) % (packetSize - 16);
        if (last == 0) last = packetSize - 16;
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(4), (ushort)last);

        // Space for the packet headers at every slice of payloadBlock bytes.
        int total = data.Length + 8;
        int slices = (total + payloadBlock - 1) / payloadBlock;
        var payload = new byte[slices * VideoHeader + total];
        int src = 0;
        for (int x = 0; x < slices; x++)
        {
            int len = Math.Min(payloadBlock, total - src);
            int dst = x * blockSize + VideoHeader;
            for (int k = 0; k < len; k++)
            {
                int i = src + k;
                payload[dst + k] = i < 8 ? header[i] : data[i - 8];
            }
            src += len;
        }

        int fecPercent = FecPercent;
        int maxDataShards = 255 * 100 / (100 + fecPercent);
        long maxPerBlock = (long)maxDataShards * blockSize;
        int blocks = (int)((payload.Length + maxPerBlock - 1) / maxPerBlock);
        if (blocks > MaxFecBlocks)
        {
            fecPercent = 0;
            blocks = MaxFecBlocks;
        }
        int aligned = (payload.Length / blocks + blockSize - 1) / blockSize * blockSize;
        uint timestamp = (uint)(Pipeline.Qpc.ToMs(frame.CaptureQpc - _epochQpc) * 90);
        uint frameIndex = (uint)frame.FrameIndex;

        lock (_sendGate)
        {
            if (_session != s) return;
            ushort lowSeq = _videoSeq;
            for (int b = 0; b < blocks; b++)
            {
                int start = b * aligned;
                int size = b == blocks - 1 ? payload.Length - start : Math.Min(aligned, payload.Length - start);
                int dataShards = (size + blockSize - 1) / blockSize;
                int pct = fecPercent;
                int parity = (dataShards * pct + 99) / 100;
                if (parity < s.MinFecPackets && pct != 0)
                {
                    parity = s.MinFecPackets;
                    pct = 100 * parity / dataShards;
                }
                if (pct == 0) parity = 0;
                int shards = dataShards + parity;
                var buffer = new byte[shards * blockSize];
                Buffer.BlockCopy(payload, start, buffer, 0, size);
                byte multiFec = (byte)((b << 4) | ((blocks - 1) << 6));
                for (int x = 0; x < dataShards; x++)
                {
                    var p = buffer.AsSpan(x * blockSize);
                    BinaryPrimitives.WriteUInt32LittleEndian(p[16..], (uint)((lowSeq + x) << 8));
                    BinaryPrimitives.WriteUInt32LittleEndian(p[20..], frameIndex);
                    byte flags = 0x1;
                    if (x == 0) flags |= 0x4;
                    if (x == dataShards - 1) flags |= 0x2;
                    p[24] = flags;
                    p[26] = 0x10;
                    p[27] = multiFec;
                }
                if (parity > 0) Encode(buffer, shards, blockSize, dataShards, parity);
                for (int x = 0; x < shards; x++)
                {
                    var p = buffer.AsSpan(x * blockSize, blockSize);
                    BinaryPrimitives.WriteUInt32LittleEndian(p[28..], (uint)((x << 12) | (dataShards << 22) | (pct << 4)));
                    p[0] = 0x90;
                    BinaryPrimitives.WriteUInt16BigEndian(p[2..], (ushort)(lowSeq + x));
                    BinaryPrimitives.WriteUInt32BigEndian(p[4..], timestamp);
                    p[27] = multiFec;
                    BinaryPrimitives.WriteUInt32LittleEndian(p[20..], frameIndex);
                    try { _video.SendTo(p, SocketFlags.None, peer); }
                    catch (SocketException ex) { Log.Debug("Video send failed: {Err}", ex.SocketErrorCode); }
                    VideoPackets++;
                }
                lowSeq = (ushort)(lowSeq + shards);
            }
            _videoSeq = lowSeq;
            VideoFrames++;
        }
    }

    private static void Encode(byte[] buffer, int shards, int blockSize, int dataShards, int parity)
    {
        var rs = StreamNative.ts_rs_new(dataShards, parity);
        try
        {
            fixed (byte* basePtr = buffer)
            {
                var ptrs = stackalloc byte*[shards];
                for (int i = 0; i < shards; i++) ptrs[i] = basePtr + i * blockSize;
                StreamNative.ts_rs_encode(rs, ptrs, shards, blockSize);
            }
        }
        finally
        {
            StreamNative.ts_rs_release(rs);
        }
    }

    // ---- audio ----

    /// <summary>One Opus packet: encrypt, send, and every fourth packet send the two parity packets.</summary>
    public void SendAudio(ReadOnlySpan<byte> opus)
    {
        var s = _session;
        var peer = _audioPeer;
        if (s == null || peer == null || _audio == null || _audioAes == null) return;
        lock (_sendGate)
        {
            if (_session != s) return;
            ushort seq = _audioSeq;
            int shard = seq % AudioData;
            var target = _audioShards[shard];
            int bytes;
            if (s.AudioEncrypted)
            {
                var iv = new byte[16];
                BinaryPrimitives.WriteUInt32BigEndian(iv, s.Launch.RiKeyId + seq);
                bytes = _audioAes.EncryptCbc(opus, iv, target, PaddingMode.PKCS7);
            }
            else
            {
                opus.CopyTo(target);
                bytes = opus.Length;
            }
            var packet = new byte[12 + bytes];
            packet[0] = 0x80;
            packet[1] = 97;
            BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2), seq);
            BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(4), _audioTimestamp);
            target.AsSpan(0, bytes).CopyTo(packet.AsSpan(12));
            try { _audio.SendTo(packet, peer); } catch (SocketException) { }
            AudioPackets++;

            if (shard == 0)
            {
                _audioBaseSeq = seq;
                _audioBaseTs = _audioTimestamp;
            }
            if (shard == AudioData - 1)
            {
                fixed (byte* s0 = _audioShards[0], s1 = _audioShards[1], s2 = _audioShards[2], s3 = _audioShards[3], s4 = _audioShards[4], s5 = _audioShards[5])
                {
                    var ptrs = stackalloc byte*[6] { s0, s1, s2, s3, s4, s5 };
                    StreamNative.ts_rs_encode(_audioRs, ptrs, AudioData + AudioParity, bytes);
                }
                for (int x = 0; x < AudioParity; x++)
                {
                    var fec = new byte[12 + 12 + bytes];
                    fec[0] = 0x80;
                    fec[1] = 127;
                    BinaryPrimitives.WriteUInt16BigEndian(fec.AsSpan(2), (ushort)(seq + x + 1));
                    fec[12] = (byte)x;
                    fec[13] = 97;
                    BinaryPrimitives.WriteUInt16BigEndian(fec.AsSpan(14), _audioBaseSeq);
                    BinaryPrimitives.WriteUInt32BigEndian(fec.AsSpan(16), _audioBaseTs);
                    _audioShards[AudioData + x].AsSpan(0, bytes).CopyTo(fec.AsSpan(24));
                    try { _audio.SendTo(fec, peer); } catch (SocketException) { }
                }
            }
            _audioSeq++;
            _audioTimestamp += (uint)s.AudioPacketMs;
        }
    }

    private ushort _audioBaseSeq;
    private uint _audioBaseTs;

    public void Dispose()
    {
        _stop = true;
        End(true);
        try { _video?.Close(); } catch { }
        try { _audio?.Close(); } catch { }
        _controlThread?.Join(2000);
        _videoRecv?.Join(1000);
        _audioRecv?.Join(1000);
        lock (_sendGate)
        {
            if (_enet != IntPtr.Zero) StreamNative.ts_enet_host_destroy(_enet);
            _enet = IntPtr.Zero;
        }
        if (_audioRs != IntPtr.Zero) StreamNative.ts_rs_release(_audioRs);
        _controlCipher?.Dispose();
        _audioAes?.Dispose();
    }
}
