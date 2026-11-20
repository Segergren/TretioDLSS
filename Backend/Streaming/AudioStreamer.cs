using Concentus;
using Concentus.Enums;
using Serilog;

namespace TretioDLSS.Streaming;

/// <summary>Captured stereo PCM to constant-bitrate Opus packets of the length the client asked for.</summary>
public sealed class AudioStreamer
{
    private const int Rate = 48000;
    private readonly IOpusEncoder _opus;
    private readonly int _frame;
    private readonly float[] _pending;
    private int _filled;
    private readonly byte[] _packet = new byte[1400];
    private readonly Action<ReadOnlyMemory<byte>> _send;
    private double _resamplePos;
    private float _lastL, _lastR;

    public AudioStreamer(int packetMs, bool highQuality, Action<ReadOnlyMemory<byte>> send)
    {
        _send = send;
        _frame = Rate * packetMs / 1000;
        _pending = new float[_frame * 2];
        _opus = OpusCodecFactory.CreateEncoder(Rate, 2, OpusApplication.OPUS_APPLICATION_RESTRICTED_LOWDELAY);
        _opus.Bitrate = highQuality ? 512000 : 96000;
        _opus.UseVBR = false;
        Log.Information("Stream audio: Opus {Kbps} kbps CBR, {Ms} ms packets", _opus.Bitrate / 1000, packetMs);
    }

    /// <summary>Interleaved stereo float at the capture rate; resampled to 48 kHz when needed.</summary>
    public void Write(ReadOnlySpan<float> interleaved, int sampleRate)
    {
        if (sampleRate == Rate)
        {
            for (int i = 0; i + 1 < interleaved.Length; i += 2) Push(interleaved[i], interleaved[i + 1]);
            return;
        }
        double step = (double)sampleRate / Rate;
        int frames = interleaved.Length / 2;
        while (_resamplePos < frames)
        {
            int i = (int)_resamplePos;
            double t = _resamplePos - i;
            float l0 = i == 0 ? _lastL : interleaved[(i - 1) * 2], r0 = i == 0 ? _lastR : interleaved[(i - 1) * 2 + 1];
            float l1 = interleaved[i * 2], r1 = interleaved[i * 2 + 1];
            Push((float)(l0 + (l1 - l0) * t), (float)(r0 + (r1 - r0) * t));
            _resamplePos += step;
        }
        _resamplePos -= frames;
        if (frames > 0)
        {
            _lastL = interleaved[(frames - 1) * 2];
            _lastR = interleaved[(frames - 1) * 2 + 1];
        }
    }

    private void Push(float l, float r)
    {
        _pending[_filled * 2] = l;
        _pending[_filled * 2 + 1] = r;
        if (++_filled < _frame) return;
        _filled = 0;
        int n = _opus.Encode(_pending, _frame, _packet, _packet.Length);
        if (n > 0) _send(_packet.AsMemory(0, n));
    }
}
