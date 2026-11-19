using System.Buffers;
using FlashCap;
using FcDevice = FlashCap.CaptureDevice;
using Serilog;
using TretioDLSS.Pipeline;

namespace TretioDLSS.Capture;

/// <summary>
/// Capture card through DirectShow (FlashCap), the alternative to Media Foundation. Same raw
/// buffers, same downstream path; selectable with source.api = dshow.
/// </summary>
public sealed class DshowSource : IVideoSource
{
    private readonly string _deviceName;
    private readonly CaptureFormat _format;
    private readonly ArrayPool<byte> _pool = ArrayPool<byte>.Create(16 * 1024 * 1024, 8);
    private readonly CpuFrameDiff _diff = new();
    private FcDevice? _device;
    private ulong _seq;
    private IFrameSink? _sink;
    private int _expected;
    private int _stride;
    private bool _loggedLayout;
    private FirstFrameWatchdog? _watchdog;

    public SourceInfo Info { get; }

    public DshowSource(string deviceName, CaptureFormat format)
    {
        _deviceName = deviceName;
        _format = format;
        var layout = format.Subtype == "YUY2" ? PixelLayout.Yuy2 : PixelLayout.Nv12;
        Info = new SourceInfo(deviceName, "uvc", format.Width, format.Height, format.Fps, layout, true);
    }

    private static PixelFormats ToFlashCap(string subtype) => subtype switch
    {
        "YUY2" => PixelFormats.YUYV,
        "MJPG" => PixelFormats.JPEG,
        _ => PixelFormats.NV12,
    };

    public void Start(IFrameSink sink)
    {
        _sink = sink;
        var devices = new CaptureDevices();
        var descriptor = devices.EnumerateDescriptors().FirstOrDefault(d => d.DeviceType == DeviceTypes.DirectShow && d.Name == _deviceName)
            ?? devices.EnumerateDescriptors().FirstOrDefault(d => d.DeviceType == DeviceTypes.DirectShow && d.Name.Contains(_deviceName, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"DirectShow does not list \"{_deviceName}\"");
        var want = ToFlashCap(_format.Subtype);
        var ch = descriptor.Characteristics.FirstOrDefault(c => c.Width == _format.Width && c.Height == _format.Height && c.PixelFormat == want
                     && FormatPicker.RateMatches((double)c.FramesPerSecond.Numerator / Math.Max(1, c.FramesPerSecond.Denominator), _format.Fps))
            ?? throw new InvalidOperationException($"DirectShow does not offer {_format.Key} on this device");
        _expected = CpuFrame.ByteSize(Info.Layout, _format.Width, _format.Height, out _stride);
        _device = descriptor.OpenAsync(ch, TranscodeFormats.DoNotTranscode, true, 1, OnBuffer).GetAwaiter().GetResult();
        _watchdog = new FirstFrameWatchdog(_deviceName, "DirectShow", _format.Key);
        _device.StartAsync().GetAwaiter().GetResult();
        sink.OnSourceEvent(SourceEvent.Started);
        Log.Information("DirectShow capture opened: {Format} on {Device}", _format.Key, _deviceName);
    }

    private void OnBuffer(PixelBufferScope scope)
    {
        long arrival = Qpc.Now;
        try
        {
            var image = scope.Buffer.ReferImage();
            int offset = 0;
            // FlashCap may wrap raw frames in a bitmap container; skip its header if present.
            if (image.Count > _expected && image.Count >= 14 && image.Array![image.Offset] == (byte)'B' && image.Array[image.Offset + 1] == (byte)'M')
                offset = BitConverter.ToInt32(image.Array, image.Offset + 10);
            if (!_loggedLayout)
            {
                _loggedLayout = true;
                _watchdog?.Arrived();
                Log.Information("DirectShow buffer {Len} bytes, expected {Expected}, header {Offset}", image.Count, _expected, offset);
            }
            if (image.Count - offset < _expected) return;
            var f = new CpuFrame(_pool, Info.Layout, _format.Width, _format.Height, _stride, _expected);
            Buffer.BlockCopy(image.Array!, image.Offset + offset, f.Data, 0, _expected);
            f.Diff = _diff.Compute(Info.Layout, f.Data, f.Width, f.Height, f.Stride);
            f.MeanDiff = _diff.LastMean;
            f.MeanLuma = _diff.LastLuma;
            f.CaptureQpc = arrival;
            f.Sequence = _seq++;
            _sink?.OnFrame(f);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "DirectShow frame failed");
        }
        finally
        {
            scope.ReleaseNow();
        }
    }

    public void Stop()
    {
        try { _device?.StopAsync().GetAwaiter().GetResult(); } catch { }
        try { _device?.Dispose(); } catch { }
        _device = null;
        _watchdog?.Dispose();
        _watchdog = null;
    }

    public void Dispose() => Stop();
}
