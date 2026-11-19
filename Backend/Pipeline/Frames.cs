using System.Buffers;
using System.Diagnostics;
using Vortice.Direct3D11;

namespace TretioDLSS.Pipeline;

public enum PixelLayout
{
    Nv12,
    Yuy2,
    Bgra8,
}

public static class Qpc
{
    public static readonly long Frequency = Stopwatch.Frequency;
    public static long Now => Stopwatch.GetTimestamp();
    public static double ToMs(long ticks) => ticks * 1000.0 / Frequency;
    public static long FromMs(double ms) => (long)(ms * Frequency / 1000.0);
}

/// <summary>A frame as a source delivers it. Owned by whoever holds it last; Dispose returns it.</summary>
public abstract class SourceFrame : IDisposable
{
    public int Width { get; protected set; }
    public int Height { get; protected set; }
    public PixelLayout Layout { get; protected set; }
    public long CaptureQpc { get; set; }
    public ulong Sequence { get; set; }

    /// <summary>Mean absolute luma difference to the previous frame, in 8-bit steps. NaN if not computed yet.</summary>
    public double Diff { get; set; } = double.NaN;

    /// <summary>Whole-frame mean difference, for scene-cut detection.</summary>
    public double MeanDiff { get; set; }

    /// <summary>Mean sampled luma, NaN when the source does not measure it.</summary>
    public double MeanLuma { get; set; } = double.NaN;
    public bool? Real { get; set; }
    public abstract void Dispose();
}

/// <summary>Raw pixel bytes in system memory (NV12, YUY2 or BGRA), tightly packed with the given stride.</summary>
public sealed class CpuFrame : SourceFrame
{
    private readonly ArrayPool<byte> _pool;
    private int _disposed;

    public byte[] Data { get; }
    public int Length { get; }
    public int Stride { get; }

    public CpuFrame(ArrayPool<byte> pool, PixelLayout layout, int width, int height, int stride, int length)
    {
        _pool = pool;
        Layout = layout;
        Width = width;
        Height = height;
        Stride = stride;
        Length = length;
        Data = pool.Rent(length);
    }

    public static int ByteSize(PixelLayout layout, int width, int height, out int stride)
    {
        switch (layout)
        {
            case PixelLayout.Nv12:
                stride = width;
                return width * height * 3 / 2;
            case PixelLayout.Yuy2:
                stride = width * 2;
                return width * height * 2;
            default:
                stride = width * 4;
                return width * height * 4;
        }
    }

    public override void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0) _pool.Return(Data);
    }
}

/// <summary>A frame that already lives on the GPU (window capture). The owner callback releases it.</summary>
public sealed class GpuFrame : SourceFrame
{
    private Action? _release;
    public ID3D11Texture2D Texture { get; }
    public int CropX { get; }
    public int CropY { get; }

    public GpuFrame(ID3D11Texture2D texture, int cropX, int cropY, int width, int height, Action release)
    {
        Texture = texture;
        CropX = cropX;
        CropY = cropY;
        Width = width;
        Height = height;
        Layout = PixelLayout.Bgra8;
        _release = release;
    }

    public override void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
}

/// <summary>A pattern frame drawn by the pipeline thread itself (GPU path test).</summary>
public sealed class DrawFrame : SourceFrame
{
    public long ContentIndex { get; }

    public DrawFrame(long contentIndex, int width, int height)
    {
        ContentIndex = contentIndex;
        Width = width;
        Height = height;
        Layout = PixelLayout.Bgra8;
    }

    public override void Dispose() { }
}

/// <summary>One-slot handoff from a source thread to the pipeline thread. Newer frames replace unread ones.</summary>
public sealed class Mailbox : IDisposable
{
    private SourceFrame? _slot;
    private readonly object _gate = new();
    public AutoResetEvent Signal { get; } = new(false);
    public long Replaced;

    public void Put(SourceFrame frame)
    {
        SourceFrame? old;
        lock (_gate)
        {
            old = _slot;
            _slot = frame;
        }
        if (old != null)
        {
            Interlocked.Increment(ref Replaced);
            old.Dispose();
        }
        Signal.Set();
    }

    public SourceFrame? Take()
    {
        lock (_gate)
        {
            var f = _slot;
            _slot = null;
            return f;
        }
    }

    public void Dispose()
    {
        Take()?.Dispose();
        Signal.Dispose();
    }
}

public interface IFrameSink
{
    /// <summary>Called on the source thread. Must return quickly.</summary>
    void OnFrame(SourceFrame frame);

    /// <summary>Source state changes: no signal, device lost, device back.</summary>
    void OnSourceEvent(SourceEvent e, string? message = null);
}

public enum SourceEvent
{
    Started,
    DeviceLost,
    DeviceBack,
    Error,
    Ended,
}

public sealed record SourceInfo(string Name, string Kind, int Width, int Height, double ContainerHz, PixelLayout Layout, bool IsCpu);

public interface IVideoSource : IDisposable
{
    SourceInfo Info { get; }
    void Start(IFrameSink sink);
    void Stop();
}
