using System.Buffers;
using Serilog;
using TretioDLSS.Pipeline;
using TretioDLSS.Platform;
using Vortice.MediaFoundation;

namespace TretioDLSS.Capture;

/// <summary>
/// Plays a local video file as if it were a live source: decoded to NV12 and delivered at the
/// file's own rate inside a fixed container rate, like a console signal. Loops at the end.
/// </summary>
public sealed class FileSource : IVideoSource
{
    private readonly string _path;
    private readonly int _container;
    private readonly ArrayPool<byte> _pool = ArrayPool<byte>.Create(32 * 1024 * 1024, 8);
    private readonly CpuFrameDiff _diff = new();
    private Thread? _thread;
    private volatile bool _stop;
    private readonly int _width, _height;
    private readonly double _fileFps;

    public SourceInfo Info { get; }

    public FileSource(string path, int containerHz)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("Video file not found", path);
        _path = path;
        _container = containerHz;
        UvcSource.EnsureStarted();
        using var reader = OpenReader();
        using var type = reader.GetCurrentMediaType(SourceReaderIndex.FirstVideoStream);
        MediaFactory.UnpackSize(type.GetUInt64(MfGuids.FrameSize), out uint w, out uint h);
        MediaFactory.UnpackRatio(type.GetUInt64(MfGuids.FrameRate), out uint n, out uint d);
        _width = (int)w & ~1;
        _height = (int)h & ~1;
        _fileFps = d == 0 ? 30 : (double)n / d;
        Info = new SourceInfo(Path.GetFileName(path), "file", _width, _height, containerHz, PixelLayout.Nv12, true);
        Log.Information("File source {File}: {W}x{H} at {Fps:0.##} fps in {Hz} Hz", path, _width, _height, _fileFps, containerHz);
    }

    private IMFSourceReader OpenReader()
    {
        using var attrs = MediaFactory.MFCreateAttributes(2);
        attrs.Set(MfGuids.EnableVideoProcessing, 1u);
        var reader = MediaFactory.MFCreateSourceReaderFromURL(_path, attrs);
        reader.SetStreamSelection(SourceReaderIndex.AllStreams, false);
        reader.SetStreamSelection(SourceReaderIndex.FirstVideoStream, true);
        using var type = MediaFactory.MFCreateMediaType();
        type.Set(MfGuids.MajorType, MfGuids.VideoMajor);
        type.Set(MfGuids.Subtype, MfGuids.Nv12);
        reader.SetCurrentMediaType(SourceReaderIndex.FirstVideoStream, type);
        return reader;
    }

    public void Start(IFrameSink sink)
    {
        _stop = false;
        _thread = new Thread(() => Run(sink)) { IsBackground = true, Name = "FileSource", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    public void Stop()
    {
        _stop = true;
        _thread?.Join(3000);
        _thread = null;
    }

    private void Run(IFrameSink sink)
    {
        try
        {
            using var reader = OpenReader();
            using var current = reader.GetCurrentMediaType(SourceReaderIndex.FirstVideoStream);
            MediaFactory.UnpackSize(current.GetUInt64(MfGuids.FrameSize), out uint rw, out uint rh);
            int srcStride = (int)current.GetUInt32(MfGuids.DefaultStride);
            if (srcStride <= 0) srcStride = (int)rw;
            int size = CpuFrame.ByteSize(PixelLayout.Nv12, _width, _height, out int stride);
            var last = new byte[size];
            bool haveFrame = false;
            using var timer = new PreciseTimer();
            long start = Qpc.Now;
            long lastContent = -1;
            ulong seq = 0;
            sink.OnSourceEvent(SourceEvent.Started);
            for (long tick = 0; !_stop; tick++)
            {
                timer.WaitUntil(start + Qpc.FromMs(tick * 1000.0 / _container));
                long content = (long)Math.Floor(tick * _fileFps / _container + 1e-9);
                if (content != lastContent)
                {
                    lastContent = content;
                    if (ReadNext(reader, last, srcStride, (int)rh, stride)) haveFrame = true;
                }
                if (!haveFrame) continue;
                var f = new CpuFrame(_pool, PixelLayout.Nv12, _width, _height, stride, size);
                Buffer.BlockCopy(last, 0, f.Data, 0, size);
                f.Diff = _diff.Compute(PixelLayout.Nv12, f.Data, _width, _height, stride);
                f.MeanDiff = _diff.LastMean;
                f.MeanLuma = _diff.LastLuma;
                f.CaptureQpc = Qpc.Now;
                f.Sequence = seq++;
                sink.OnFrame(f);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "File source failed");
            sink.OnSourceEvent(SourceEvent.Error, "The video file could not be played: " + ex.Message);
        }
    }

    private unsafe bool ReadNext(IMFSourceReader reader, byte[] dst, int srcStride, int srcHeight, int stride)
    {
        for (int attempt = 0; attempt < 2; attempt++)
        {
            var sample = reader.ReadSample(SourceReaderIndex.FirstVideoStream, SourceReaderControlFlag.None, out _, out var flags, out _);
            if ((flags & SourceReaderFlag.EndOfStream) != 0)
            {
                sample?.Dispose();
                reader.SetCurrentPosition(0);
                continue;
            }
            if (sample == null) return false;
            using (sample)
            using (var buffer = sample.ConvertToContiguousBuffer())
            {
                buffer.Lock(out IntPtr ptr, out _, out int len);
                try
                {
                    byte* src = (byte*)ptr;
                    fixed (byte* d = dst)
                    {
                        for (int y = 0; y < _height; y++)
                            Buffer.MemoryCopy(src + (long)y * srcStride, d + (long)y * stride, stride, _width);
                        byte* srcUv = src + (long)srcStride * srcHeight;
                        byte* dstUv = d + (long)stride * _height;
                        for (int y = 0; y < _height / 2; y++)
                            Buffer.MemoryCopy(srcUv + (long)y * srcStride, dstUv + (long)y * stride, stride, _width);
                    }
                }
                finally
                {
                    buffer.Unlock();
                }
            }
            return true;
        }
        return false;
    }

    public void Dispose() => Stop();
}
