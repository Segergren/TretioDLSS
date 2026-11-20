using NAudio.CoreAudioApi;
using NAudio.Wave;
using Serilog;

namespace TretioDLSS.Audio;

public sealed record AudioDevice(string Id, string Name, bool IsDefault, string? ContainerId);

public static class AudioDevices
{
    public static List<AudioDevice> List(DataFlow flow)
    {
        var list = new List<AudioDevice>();
        try
        {
            using var en = new MMDeviceEnumerator();
            string? defaultId = null;
            if (en.TryGetDefaultAudioEndpoint(flow, Role.Multimedia, out var def))
            {
                defaultId = def.ID;
                def.Dispose();
            }
            foreach (var d in en.EnumerateAudioEndPoints(flow, DeviceState.Active))
            {
                using (d)
                {
                    string? container = null;

                    list.Add(new AudioDevice(d.ID, d.FriendlyName, d.ID == defaultId, container));
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Audio device enumeration failed");
        }
        return list;
    }

    /// <summary>The capture endpoint that belongs to a video capture device, matched by name.</summary>
    public static AudioDevice? MatchCaptureCard(List<AudioDevice> inputs, string videoDeviceName)
    {
        if (string.IsNullOrEmpty(videoDeviceName)) return null;
        var tokens = videoDeviceName.Split([' ', '(', ')', '-', '_'], StringSplitOptions.RemoveEmptyEntries)
            .Where(t => t.Length >= 3 && !t.Equals("video", StringComparison.OrdinalIgnoreCase) && !t.Equals("capture", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        return inputs.FirstOrDefault(i => i.Name.Contains(videoDeviceName, StringComparison.OrdinalIgnoreCase))
            ?? inputs.FirstOrDefault(i => tokens.Any(t => i.Name.Contains(t, StringComparison.OrdinalIgnoreCase)))
            ?? inputs.FirstOrDefault(i => i.Name.Contains("Digital Audio Interface", StringComparison.OrdinalIgnoreCase) || i.Name.Contains("USB3", StringComparison.OrdinalIgnoreCase) && i.Name.Contains("Video", StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>
/// Float ring buffer plus a linear resampler whose ratio is nudged (at most 0.5%) to hold the
/// buffer at its target level, so two device clocks that drift apart never click.
/// </summary>
public sealed class AdaptiveResampler
{
    private readonly float[] _ring;
    private readonly int _channels;
    private long _write;
    private double _readPos;
    private readonly double _nominal;
    private double _integral;
    private readonly object _gate = new();

    public double TargetFrames { get; set; }
    public double Ratio { get; private set; }
    public long Underruns { get; private set; }
    public long Overruns { get; private set; }
    public const double MaxCorrection = 0.005;

    public AdaptiveResampler(int channels, double inRate, double outRate, double capacitySeconds = 2)
    {
        _channels = channels;
        _ring = new float[(int)(inRate * capacitySeconds) * channels];
        _nominal = inRate / outRate;
        Ratio = _nominal;
    }

    /// <summary>Frames buffered (input rate).</summary>
    public double LevelFrames
    {
        get { lock (_gate) return _write - _readPos; }
    }

    public void Write(ReadOnlySpan<float> interleaved)
    {
        lock (_gate)
        {
            int frames = interleaved.Length / _channels;
            int cap = _ring.Length / _channels;
            if (_write + frames - _readPos > cap)
            {
                // Too much buffered: drop the oldest audio.
                Overruns++;
                _readPos = _write + frames - TargetFrames;
            }
            for (int f = 0; f < frames; f++)
            {
                long slot = (_write + f) % cap;
                for (int c = 0; c < _channels; c++) _ring[slot * _channels + c] = interleaved[f * _channels + c];
            }
            _write += frames;
        }
    }

    private bool _primed;
    private double _smoothLevel = double.NaN;

    /// <summary>
    /// Fills output frames. Plays silence until the buffer first reaches its target (and again after an
    /// underrun), then reads with a drift correction from a PI controller on the smoothed level.
    /// </summary>
    public void Read(Span<float> output)
    {
        lock (_gate)
        {
            int frames = output.Length / _channels;
            int cap = _ring.Length / _channels;
            double level = _write - _readPos;
            if (!_primed)
            {
                if (level < TargetFrames)
                {
                    output.Clear();
                    return;
                }
                _primed = true;
                _smoothLevel = level;
            }
            // Packets arrive in bursts, so the controller sees a smoothed level (about a second).
            _smoothLevel += (level - _smoothLevel) * 0.01;
            double error = (_smoothLevel - TargetFrames) / Math.Max(1, TargetFrames);
            _integral = Math.Clamp(_integral + error * 0.00002, -MaxCorrection, MaxCorrection);
            double correction = Math.Clamp(error * 0.002 + _integral, -MaxCorrection, MaxCorrection);
            Ratio = _nominal * (1 + correction);
            for (int f = 0; f < frames; f++)
            {
                if (_readPos + 1 >= _write)
                {
                    Underruns++;
                    _primed = false;
                    output[(f * _channels)..].Clear();
                    return;
                }
                long i0 = (long)Math.Floor(_readPos);
                double frac = _readPos - i0;
                long s0 = i0 % cap, s1 = (i0 + 1) % cap;
                for (int c = 0; c < _channels; c++)
                    output[f * _channels + c] = (float)(_ring[s0 * _channels + c] * (1 - frac) + _ring[s1 * _channels + c] * frac);
                _readPos += Ratio;
            }
        }
    }
}

public delegate void AudioTap(ReadOnlySpan<float> interleaved, int sampleRate);

/// <summary>Moves the capture card's sound to the output device with a controlled delay.</summary>
public sealed class AudioBridge : IDisposable
{
    /// <summary>Receives the captured stereo samples as they arrive, for the TV stream.</summary>
    public AudioTap? Tap { get; set; }

    /// <summary>Silences the local output, for example while the sound goes to the TV.</summary>
    public bool MuteLocal { get; set; }

    private const double BaseBufferMs = 30;
    private WasapiRecorder? _recorder;
    private WasapiPlayer? _player;
    private AdaptiveResampler? _resampler;
    private int _inChannels;
    private WaveFormat? _inFormat;
    private float[] _convert = new float[48000 * 2];
    private double _extraDelayMs;
    private float _volume = 1;
    private Timer? _logTimer;
    private (double MinMs, double MaxMs, double MinRatio, double MaxRatio, DateTime Since) _stats;

    public string InputName { get; private set; } = "";
    public string OutputName { get; private set; } = "";
    public bool Running { get; private set; }
    public double PathLatencyMs { get; private set; }
    public double LevelMs => _resampler == null || _inFormat == null ? 0 : _resampler.LevelFrames * 1000.0 / _inFormat.SampleRate;
    public long Underruns => _resampler?.Underruns ?? 0;
    public double Ratio => _resampler?.Ratio ?? 1;

    /// <summary>The resampler's drift correction alone, 1.0 when both clocks agree.</summary>
    public double Correction => _resampler == null || _inFormat == null || _outRate == 0 ? 1 : _resampler.Ratio / (_inFormat.SampleRate / (double)_outRate);
    private int _outRate;
    public double TargetMs => BaseBufferMs + _extraDelayMs;
    public string? Error { get; private set; }

    public void SetVolume(double v) => _volume = (float)Math.Clamp(v, 0, 1);

    private double _sumL, _sumR;
    private long _levelFrames;

    /// <summary>Input level per channel in dBFS since the last call.</summary>
    public (double Left, double Right) TakeChannelLevels()
    {
        double l, r;
        long n;
        lock (_levelGate) { (l, r, n) = (_sumL, _sumR, _levelFrames); _sumL = _sumR = 0; _levelFrames = 0; }
        static double Db(double sum, long n) => n == 0 || sum <= 0 ? -120 : 10 * Math.Log10(sum / n);
        return (Db(l, n), Db(r, n));
    }

    private readonly object _levelGate = new();

    /// <summary>Extra delay on top of the base buffer (picture delay minus the audio path, plus the user offset).</summary>
    public void SetExtraDelay(double ms)
    {
        _extraDelayMs = Math.Max(0, ms);
        if (_resampler != null && _inFormat != null) _resampler.TargetFrames = (BaseBufferMs + _extraDelayMs) * _inFormat.SampleRate / 1000.0;
    }

    public void Start(string inputId, string? outputId)
    {
        Stop();
        Error = null;
        try
        {
            using var en = new MMDeviceEnumerator();
            // "loopback" captures what the default output plays: the stand-in when a machine has no input.
            bool loopback = inputId == "loopback";
            var input = loopback ? en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia) : en.GetDevice(inputId);
            var output = string.IsNullOrEmpty(outputId) || outputId == "default" ? en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia) : en.GetDevice(outputId);
            InputName = (loopback ? "Loopback of " : "") + input.FriendlyName;
            OutputName = output.FriendlyName;

            var builder = new WasapiRecorderBuilder().WithDevice(input).WithSharedMode().WithEventSync();
            _recorder = (loopback ? builder.WithLoopbackCapture() : builder.WithLowLatency(true)).Build();
            _inFormat = _recorder.WaveFormat;
            _inChannels = _inFormat.Channels;

            _player = new WasapiPlayerBuilder().WithDevice(output).WithSharedMode().WithEventSync().WithLowLatency(true).WithLatency(10).Build();
            var outFormat = WaveFormat.CreateIeeeFloatWaveFormat(_player.DeviceMixFormat.SampleRate, 2);
            _outRate = outFormat.SampleRate;
            _resampler = new AdaptiveResampler(2, _inFormat.SampleRate, outFormat.SampleRate) { TargetFrames = (BaseBufferMs + _extraDelayMs) * _inFormat.SampleRate / 1000.0 };
            _player.Init(new Provider(this, outFormat));
            _recorder.DataAvailable += OnData;
            _recorder.StartRecording();
            _player.Play();
            Running = true;
            PathLatencyMs = BaseBufferMs + _recorder.LatencyMilliseconds + _player.LatencyMilliseconds;
            Log.Information("Audio: {In} ({InFmt}, low latency {InLow}) to {Out} ({OutRate} Hz, low latency {OutLow}), path {Path:0} ms",
                InputName, _inFormat, _recorder.LowLatencyActive, OutputName, outFormat.SampleRate, _player.LowLatencyActive, PathLatencyMs);
            _stats = (double.MaxValue, 0, 1, 1, DateTime.UtcNow);
            _logTimer = new Timer(_ =>
            {
                var c = Correction;
                _stats = (Math.Min(_stats.MinMs, LevelMs), Math.Max(_stats.MaxMs, LevelMs), Math.Min(_stats.MinRatio, c), Math.Max(_stats.MaxRatio, c), _stats.Since);
                var (l, r) = TakeChannelLevels();
                Log.Debug("Audio buffer {Level:0.0} ms, target {Target:0.0} ms, ratio {Ratio:0.00000}, underruns {U}, input left {L:0.0} dB, right {R:0.0} dB", LevelMs, TargetMs, Correction, Underruns, l, r);
            }, null, 1000, 1000);
            input.Dispose();
            output.Dispose();
        }
        catch (Exception ex)
        {
            Error = ex.HResult == unchecked((int)0x80070005)
                ? "Windows blocks microphone access for desktop apps, so the card's sound is silent. Turn it on in Settings, Privacy and security, Microphone."
                : "Sound could not start: " + ex.Message;
            Log.Warning(ex, "Audio bridge failed to start");
            Stop();
        }
    }

    private void OnData(ReadOnlySpan<byte> data, AudioClientBufferFlags flags, long devicePosition, long qpc)
    {
        if (_resampler == null || _inFormat == null) return;
        int frames = data.Length / _inFormat.BlockAlign;
        if (_convert.Length < frames * 2) _convert = new float[frames * 2];
        bool silent = (flags & AudioClientBufferFlags.Silent) != 0;
        bool isFloat = _inFormat.Encoding == WaveFormatEncoding.IeeeFloat || (_inFormat is WaveFormatExtensible ext && ext.SubFormat == new Guid("00000003-0000-0010-8000-00aa00389b71"));
        double sl = 0, sr = 0;
        for (int f = 0; f < frames; f++)
        {
            float l = 0, r = 0;
            if (!silent)
            {
                int o = f * _inFormat.BlockAlign;
                l = Sample(data, o, isFloat);
                r = _inChannels > 1 ? Sample(data, o + _inFormat.BitsPerSample / 8, isFloat) : l;
            }
            _convert[f * 2] = l;
            _convert[f * 2 + 1] = r;
            sl += l * l;
            sr += r * r;
        }
        lock (_levelGate) { _sumL += sl; _sumR += sr; _levelFrames += frames; }
        Tap?.Invoke(_convert.AsSpan(0, frames * 2), _inFormat.SampleRate);
        _resampler.Write(_convert.AsSpan(0, frames * 2));
    }

    private float Sample(ReadOnlySpan<byte> d, int o, bool isFloat) => _inFormat!.BitsPerSample switch
    {
        32 when isFloat => BitConverter.ToSingle(d.Slice(o, 4)),
        32 => BitConverter.ToInt32(d.Slice(o, 4)) / 2147483648f,
        24 => ((d[o] << 8 | d[o + 1] << 16 | d[o + 2] << 24) >> 8) / 8388608f,
        16 => BitConverter.ToInt16(d.Slice(o, 2)) / 32768f,
        _ => 0,
    };

    private sealed class Provider(AudioBridge bridge, WaveFormat format) : IWaveProvider
    {
        private float[] _buf = new float[4096];
        public WaveFormat WaveFormat => format;

        public int Read(Span<byte> buffer)
        {
            int samples = buffer.Length / 4;
            if (_buf.Length < samples) _buf = new float[samples];
            var span = _buf.AsSpan(0, samples);
            bridge._resampler?.Read(span);
            float v = bridge.MuteLocal ? 0 : bridge._volume;
            for (int i = 0; i < samples; i++) BitConverter.TryWriteBytes(buffer.Slice(i * 4, 4), span[i] * v);
            return buffer.Length;
        }
    }

    public void Stop()
    {
        if (_logTimer != null && _stats.MaxMs > 0)
            Log.Information("Audio session {Min:0} min: buffer {Lo:0.0} to {Hi:0.0} ms, underruns {U}, correction {Rlo:0.00000} to {Rhi:0.00000}",
                (DateTime.UtcNow - _stats.Since).TotalMinutes, _stats.MinMs, _stats.MaxMs, Underruns, _stats.MinRatio, _stats.MaxRatio);
        Running = false;
        _logTimer?.Dispose();
        _logTimer = null;
        try { _recorder?.StopRecording(); } catch { }
        try { _player?.Stop(); } catch { }
        _recorder?.Dispose();
        _player?.Dispose();
        _recorder = null;
        _player = null;
    }

    public void Dispose() => Stop();
}
