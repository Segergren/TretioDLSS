using TretioDLSS.Graphics;
using TretioDLSS.Output;

namespace TretioDLSS.Streaming;

/// <summary>Output for the TV: frames are composed at the stream size and handed to NVENC, no window, no display.</summary>
public sealed class StreamSink : OutputSink
{
    private readonly RenderTexture _target;
    private readonly NvencEncoder _encoder;
    private readonly double _fps;

    public StreamSink(GraphicsDevice g, StreamFormat format, Action<EncodedFrame> output) : base(g)
    {
        _encoder = new NvencEncoder(g, format.Codec, format.Width, format.Height, format.Fps, format.BitrateKbps, output);
        Width = _encoder.Width;
        Height = _encoder.Height;
        _fps = format.Fps;
        _target = new RenderTexture(g.Device, Width, Height);
    }

    public override double RefreshHz => _fps;

    public NvencEncoder Encoder => _encoder;

    public override void Present(RenderTexture frame, PresentedFrame info, int index)
    {
        Compose(_target.Rtv, frame);
        if (DumpPathFor?.Invoke(index) is string path) DumpTexture(_target.Texture, path);
        _encoder.Submit(_target, info.Qpc);
        G.Context.Flush();
        Record(info);
    }

    public override void Dispose()
    {
        _encoder.Dispose();
        _target.Dispose();
        base.Dispose();
    }
}

/// <summary>What the client asked for: size, rate, codec (0 H.264, 1 HEVC, 2 AV1) and bitrate.</summary>
public sealed record StreamFormat(int Width, int Height, int Fps, int Codec, int BitrateKbps);
