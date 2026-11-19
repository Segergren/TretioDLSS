using System.Buffers;
using System.Text.Json;
using Serilog;
using TretioDLSS.Pipeline;
using TretioDLSS.Platform;

namespace TretioDLSS.Capture;

/// <summary>Index file of a raw recording: frames.bin holds the buffers back to back.</summary>
public sealed class RawIndex
{
    public string Layout { get; set; } = "Nv12";
    public int Width { get; set; }
    public int Height { get; set; }
    public int Stride { get; set; }
    public int FrameSize { get; set; }
    public double ContainerHz { get; set; }
    public string Device { get; set; } = "";
    public string Format { get; set; } = "";
    public List<double> TimesMs { get; set; } = new();
}

/// <summary>Writes the buffers of any CPU source exactly as delivered, with their timestamps.</summary>
public sealed class RawRecorder : IFrameSink, IDisposable
{
    private readonly string _dir;
    private readonly FileStream _data;
    private readonly RawIndex _index = new();
    private long _first;
    private readonly ManualResetEventSlim _done = new();
    private readonly double _seconds;
    private bool _closed;

    public int Frames => _index.TimesMs.Count;

    public RawRecorder(string dir, double seconds, SourceInfo info, string format)
    {
        _dir = dir;
        _seconds = seconds;
        Directory.CreateDirectory(dir);
        _data = new FileStream(Path.Combine(dir, "frames.bin"), FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 20);
        _index.Width = info.Width;
        _index.Height = info.Height;
        _index.ContainerHz = info.ContainerHz;
        _index.Layout = info.Layout.ToString();
        _index.Device = info.Name;
        _index.Format = format;
    }

    public void OnFrame(SourceFrame frame)
    {
        using (frame)
        {
            if (frame is not CpuFrame cf || _closed) return;
            if (_first == 0) _first = cf.CaptureQpc;
            double t = Qpc.ToMs(cf.CaptureQpc - _first);
            if (t > _seconds * 1000)
            {
                _done.Set();
                return;
            }
            _index.Stride = cf.Stride;
            _index.FrameSize = cf.Length;
            _data.Write(cf.Data, 0, cf.Length);
            _index.TimesMs.Add(t);
        }
    }

    public void OnSourceEvent(SourceEvent e, string? message = null)
    {
        if (e is SourceEvent.DeviceLost or SourceEvent.Error)
        {
            Log.Warning("Recording source problem: {Message}", message);
            _done.Set();
        }
    }

    public bool Wait(TimeSpan timeout) => _done.Wait(timeout);

    public void Dispose()
    {
        if (_closed) return;
        _closed = true;
        _data.Dispose();
        File.WriteAllText(Path.Combine(_dir, "index.json"), JsonSerializer.Serialize(_index, Core.Json.Options));
        _done.Dispose();
    }
}

/// <summary>Replays a raw recording with its original timing, as a CPU source.</summary>
public sealed class RawDumpSource : IVideoSource
{
    private readonly string _dir;
    private readonly RawIndex _index;
    private readonly ArrayPool<byte> _pool = ArrayPool<byte>.Create(16 * 1024 * 1024, 8);
    private readonly CpuFrameDiff _diff = new();
    private Thread? _thread;
    private volatile bool _stop;

    public SourceInfo Info { get; }
    public bool Loop { get; set; } = true;

    public RawDumpSource(string dir)
    {
        _dir = dir;
        var indexPath = Path.Combine(dir, "index.json");
        if (!File.Exists(indexPath)) throw new FileNotFoundException("Not a raw recording (index.json missing)", indexPath);
        _index = JsonSerializer.Deserialize<RawIndex>(File.ReadAllText(indexPath), Core.Json.Options) ?? throw new InvalidDataException("Bad index.json");
        var layout = Enum.Parse<PixelLayout>(_index.Layout, true);
        Info = new SourceInfo($"Recording {Path.GetFileName(dir.TrimEnd('\\', '/'))}", "raw", _index.Width, _index.Height, _index.ContainerHz, layout, true);
    }

    public void Start(IFrameSink sink)
    {
        _stop = false;
        _thread = new Thread(() => Run(sink)) { IsBackground = true, Name = "RawReplay" };
        _thread.Start();
    }

    public void Stop()
    {
        _stop = true;
        _thread?.Join(3000);
    }

    private void Run(IFrameSink sink)
    {
        using var data = new FileStream(Path.Combine(_dir, "frames.bin"), FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
        using var timer = new PreciseTimer();
        var layout = Info.Layout;
        ulong seq = 0;
        sink.OnSourceEvent(SourceEvent.Started);
        double loopOffset = 0;
        long start = Qpc.Now;
        do
        {
            data.Position = 0;
            for (int i = 0; i < _index.TimesMs.Count && !_stop; i++)
            {
                timer.WaitUntil(start + Qpc.FromMs(loopOffset + _index.TimesMs[i]));
                var f = new CpuFrame(_pool, layout, _index.Width, _index.Height, _index.Stride, _index.FrameSize);
                data.ReadExactly(f.Data, 0, _index.FrameSize);
                f.Diff = _diff.Compute(layout, f.Data, f.Width, f.Height, f.Stride);
                f.MeanDiff = _diff.LastMean;
                f.MeanLuma = _diff.LastLuma;
                f.CaptureQpc = Qpc.Now;
                f.Sequence = seq++;
                sink.OnFrame(f);
            }
            loopOffset += (_index.TimesMs.LastOrDefault() + 1000.0 / Math.Max(1, _index.ContainerHz));
        } while (Loop && !_stop);
        sink.OnSourceEvent(SourceEvent.Ended);
    }

    public void Dispose() => Stop();
}
