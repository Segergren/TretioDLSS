using System.Buffers;
using System.Runtime.InteropServices;
using Serilog;
using TretioDLSS.Pipeline;
using Vortice.MediaFoundation;

namespace TretioDLSS.Capture;

/// <summary>
/// Capture card through the Media Foundation source reader. Native subtype into
/// system memory, our own upload, QPC timestamps on arrival. Reopens the device when it returns.
/// </summary>
public sealed class UvcSource : IVideoSource
{
    private static readonly object MfGate = new();
    private static bool _mfStarted;

    private readonly string _deviceId;
    private readonly CaptureFormat _format;
    private readonly ArrayPool<byte> _pool = ArrayPool<byte>.Create(16 * 1024 * 1024, 8);
    private readonly CpuFrameDiff _diff = new();
    private Thread? _thread;
    private volatile bool _stop;

    public SourceInfo Info { get; }
    public string Api => "Media Foundation";

    public UvcSource(string deviceId, string name, CaptureFormat format)
    {
        _deviceId = deviceId;
        _format = format;
        var layout = format.Subtype == "YUY2" ? PixelLayout.Yuy2 : PixelLayout.Nv12;
        Info = new SourceInfo(name, "uvc", format.Width, format.Height, format.Fps, layout, true);
    }

    public static void EnsureStarted()
    {
        lock (MfGate)
        {
            if (_mfStarted) return;
            MediaFactory.MFStartup(true).CheckError();
            _mfStarted = true;
        }
    }

    /// <summary>All video capture devices with their native formats.</summary>
    public static List<CaptureDevice> Enumerate()
    {
        EnsureStarted();
        var result = new List<CaptureDevice>();
        using var attrs = MediaFactory.MFCreateAttributes(1);
        attrs.Set(MfGuids.SourceType, MfGuids.SourceTypeVidcap);
        using var collection = MediaFactory.MFEnumDeviceSources(attrs);
        foreach (var activate in collection)
        {
            using (activate)
            {
                string name = "Capture device", link = "";
                try
                {
                    name = activate.FriendlyName;
                    link = activate.GetAllocatedString(MfGuids.VidcapSymbolicLink) ?? "";
                    result.Add(new CaptureDevice(link, name, ReadFormats(activate)));
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "Could not read formats of {Name}", name);
                    if (link.Length > 0) result.Add(new CaptureDevice(link, name, []));
                }
            }
        }
        return result;
    }

    private static List<CaptureFormat> ReadFormats(IMFActivate activate)
    {
        var list = new List<CaptureFormat>();
        using var source = activate.ActivateObject<IMFMediaSource>();
        try
        {
            using var reader = MediaFactory.MFCreateSourceReaderFromMediaSource(source, null);
            for (int i = 0; ; i++)
            {
                IMFMediaType type;
                try { type = reader.GetNativeMediaType(SourceReaderIndex.FirstVideoStream, i); }
                catch { break; }
                using (type)
                {
                    var f = Describe(type);
                    if (f != null && !list.Contains(f)) list.Add(f);
                }
            }
        }
        finally
        {
            try { source.Shutdown(); } catch { }
        }
        return list.OrderByDescending(f => (long)f.Width * f.Height).ThenByDescending(f => f.Fps).ThenBy(f => f.Subtype).ToList();
    }

    internal static CaptureFormat? Describe(IMFMediaType type)
    {
        if (type.GetGUID(MfGuids.MajorType) != MfGuids.VideoMajor) return null;
        var sub = type.GetGUID(MfGuids.Subtype);
        MediaFactory.UnpackSize(type.GetUInt64(MfGuids.FrameSize), out uint w, out uint h);
        MediaFactory.UnpackRatio(type.GetUInt64(MfGuids.FrameRate), out uint num, out uint den);
        double fps = den == 0 ? 0 : Math.Round((double)num / den, 3);
        return new CaptureFormat((int)w, (int)h, fps, MfGuids.Name(sub));
    }

    public void Start(IFrameSink sink)
    {
        _stop = false;
        _thread = new Thread(() => Run(sink)) { IsBackground = true, Name = "UvcCapture", Priority = ThreadPriority.Highest };
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
        EnsureStarted();
        bool lost = false;
        while (!_stop)
        {
            IMFMediaSource? source = null;
            IMFSourceReader? reader = null;
            try
            {
                (source, reader) = Open();
                if (lost)
                {
                    sink.OnSourceEvent(SourceEvent.DeviceBack);
                    lost = false;
                }
                else
                {
                    sink.OnSourceEvent(SourceEvent.Started);
                }
                ReadLoop(reader, sink);
            }
            catch (Exception ex) when (!_stop)
            {
                string msg = Describe(ex);
                if (!lost) Log.Warning(ex, "Capture device problem");
                lost = true;
                sink.OnSourceEvent(SourceEvent.DeviceLost, msg);
            }
            finally
            {
                reader?.Dispose();
                try { source?.Shutdown(); } catch { }
                source?.Dispose();
            }
            for (int i = 0; i < 10 && !_stop; i++) Thread.Sleep(100);
        }
    }

    internal static string Describe(Exception ex)
    {
        uint hr = (uint)ex.HResult;
        return hr switch
        {
            0x80070005 => "Windows blocks camera access for desktop apps. Turn it on in Settings, Privacy and security, Camera.",
            0xC00D3704 or 0x80070020 => "The capture card is in use by another app. Close OBS or the card's own app.",
            0xC00D36B5 or 0x80070490 or 0xC00D3EA2 => "The capture card is not connected. Waiting for it to come back.",
            _ => "The capture card stopped delivering pictures. Waiting for it to come back.",
        };
    }

    private (IMFMediaSource, IMFSourceReader) Open()
    {
        using var attrs = MediaFactory.MFCreateAttributes(2);
        attrs.Set(MfGuids.SourceType, MfGuids.SourceTypeVidcap);
        attrs.Set(MfGuids.VidcapSymbolicLink, _deviceId);
        var source = MediaFactory.MFCreateDeviceSource(attrs);
        using var readerAttrs = MediaFactory.MFCreateAttributes(3);
        readerAttrs.Set(MfGuids.LowLatency, 1u);
        bool mjpg = _format.Subtype == "MJPG";
        if (!mjpg) readerAttrs.Set(MfGuids.DisableConverters, 1u);
        else readerAttrs.Set(MfGuids.EnableVideoProcessing, 1u);
        var reader = MediaFactory.MFCreateSourceReaderFromMediaSource(source, readerAttrs);

        IMFMediaType? chosen = null;
        for (int i = 0; ; i++)
        {
            IMFMediaType type;
            try { type = reader.GetNativeMediaType(SourceReaderIndex.FirstVideoStream, i); }
            catch { break; }
            var f = Describe(type);
            if (f != null && f.Width == _format.Width && f.Height == _format.Height && f.Subtype == _format.Subtype && FormatPicker.RateMatches(f.Fps, _format.Fps))
            {
                chosen = type;
                break;
            }
            type.Dispose();
        }
        if (chosen == null) throw new InvalidOperationException($"The card does not offer {_format.Key}");
        ulong frameSize = chosen.GetUInt64(MfGuids.FrameSize), frameRate = chosen.GetUInt64(MfGuids.FrameRate);
        reader.SetCurrentMediaType(SourceReaderIndex.FirstVideoStream, chosen);
        chosen.Dispose();
        if (mjpg)
        {
            // Let the reader insert the MJPEG decoder and hand out NV12.
            using var outType = MediaFactory.MFCreateMediaType();
            outType.Set(MfGuids.MajorType, MfGuids.VideoMajor);
            outType.Set(MfGuids.Subtype, MfGuids.Nv12);
            outType.Set(MfGuids.FrameSize, frameSize);
            outType.Set(MfGuids.FrameRate, frameRate);
            reader.SetCurrentMediaType(SourceReaderIndex.FirstVideoStream, outType);
        }
        string output = "?";
        try
        {
            using var current = reader.GetCurrentMediaType(SourceReaderIndex.FirstVideoStream);
            output = Describe(current)?.Key ?? "?";
        }
        catch { }
        Log.Information("Capture opened: {Format} on {Device}, reader output {Output}", _format.Key, Info.Name, output);
        return (source, reader);
    }

    private unsafe void ReadLoop(IMFSourceReader reader, IFrameSink sink)
    {
        var layout = Info.Layout;
        int size = CpuFrame.ByteSize(layout, _format.Width, _format.Height, out int stride);
        ulong seq = 0;
        long first = 0;
        var histogram = new int[12];
        long lastArrival = 0, lastLog = Environment.TickCount64;
        _diff.Reset();
        using var watchdog = new FirstFrameWatchdog(Info.Name, "Media Foundation", _format.Key);
        while (!_stop)
        {
            var sample = reader.ReadSample(SourceReaderIndex.FirstVideoStream, SourceReaderControlFlag.None, out _, out var flags, out long ts);
            long arrival = Pipeline.Qpc.Now;
            if ((flags & SourceReaderFlag.Error) != 0) throw new IOException("Capture stream error");
            if ((flags & SourceReaderFlag.EndOfStream) != 0) throw new IOException("Capture stream ended");
            if (sample == null) continue;
            using (sample)
            {
                if (first == 0)
                {
                    first = arrival;
                    watchdog.Arrived();
                    Log.Information("First capture frame arrived");
                }
                var frame = new CpuFrame(_pool, layout, _format.Width, _format.Height, stride, size);
                using var buffer = sample.GetBufferByIndex(0);
                Copy(buffer, frame.Data, size, stride, layout, _format.Height);
                frame.Diff = _diff.Compute(layout, frame.Data, _format.Width, _format.Height, stride);
                frame.MeanDiff = _diff.LastMean;
                frame.MeanLuma = _diff.LastLuma;
                frame.CaptureQpc = arrival;
                frame.Sequence = seq++;
                sink.OnFrame(frame);
            }
            if (lastArrival != 0)
            {
                double ms = Pipeline.Qpc.ToMs(arrival - lastArrival);
                histogram[Math.Clamp((int)(ms / 4), 0, histogram.Length - 1)]++;
            }
            lastArrival = arrival;
            if (Environment.TickCount64 - lastLog > 1000)
            {
                lastLog = Environment.TickCount64;
                Log.Debug("Capture inter-arrival histogram (4 ms buckets): {Hist}", string.Join(",", histogram));
                Array.Clear(histogram);
            }
        }
    }

    private static bool _loggedBuffer;

    private static unsafe void Copy(IMFMediaBuffer buffer, byte[] dst, int size, int stride, PixelLayout layout, int height)
    {
        IMF2DBuffer2? b2 = null;
        try { b2 = buffer.QueryInterfaceOrNull<IMF2DBuffer2>(); } catch { }
        if (b2 != null)
        {
            using (b2)
            {
                b2.Lock2DSize(Buffer2DLockFlags.Read, out IntPtr scan0, out int pitch, out IntPtr start, out int bufLen);
                try
                {
                    if (!_loggedBuffer)
                    {
                        _loggedBuffer = true;
                        Log.Information("Capture buffer: pitch {Pitch}, length {Length}, expected stride {Stride}, size {Size}", pitch, bufLen, stride, size);
                    }
                    int rows = layout == PixelLayout.Nv12 ? height * 3 / 2 : height;
                    int row = Math.Min(stride, Math.Abs(pitch));
                    byte* lo = (byte*)start, hi = lo + bufLen;
                    fixed (byte* d = dst)
                    {
                        for (int y = 0; y < rows; y++)
                        {
                            byte* src = (byte*)scan0 + (long)y * pitch;
                            if (src < lo || src + row > hi) break;
                            Buffer.MemoryCopy(src, d + (long)y * stride, stride, row);
                        }
                    }
                }
                finally
                {
                    b2.Unlock2D();
                }
            }
            return;
        }
        buffer.Lock(out IntPtr ptr, out _, out int length);
        try
        {
            Marshal.Copy(ptr, dst, 0, Math.Min(length, size));
        }
        finally
        {
            buffer.Unlock();
        }
    }

    public void Dispose() => Stop();
}
