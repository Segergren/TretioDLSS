using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Serilog;
using TretioDLSS.Capture;
using TretioDLSS.Core;
using TretioDLSS.Engines;
using TretioDLSS.Output;
using TretioDLSS.Pipeline;
using TretioDLSS.Platform;

namespace TretioDLSS.App;

internal sealed record StartRequest(Settings Settings, List<CaptureDevice> Devices, bool Tearing, string? Icon, double PreviewFps);

internal sealed record PictureSnapshot(
    long SourceFrames, long RealFrames, long QuietReal, long Generated, long Held, long Dropped, long Late,
    CadenceState CadenceState, double ClockPeriodMs, double MedianDelayMs, SourceInfo? SourceInfo, string CadenceLabel,
    long Hitches, long Relocks, int ContentW, int ContentH, double[] Stages, bool CompareMode, string UpscalerStatus,
    string NeuralLine, bool NoSignal, bool SmoothMotion, bool HasNeuralStage, bool NeuralRuntimeMissing)
{
    public static readonly PictureSnapshot Empty = new(0, 0, 0, 0, 0, 0, 0, CadenceState.Unlocked, 0, 0, null, "", 0, 0, 0, 0,
        new double[5], false, "", "", false, false, false, false);

    public static PictureSnapshot Of(PipelineHost p)
    {
        var c = p.Counters;
        return new(Interlocked.Read(ref c.SourceFrames), Interlocked.Read(ref c.RealFrames), Interlocked.Read(ref c.QuietReal),
            Interlocked.Read(ref c.Generated), Interlocked.Read(ref c.Held), Interlocked.Read(ref c.Dropped), Interlocked.Read(ref c.Late),
            p.CadenceState, p.ClockPeriodMs, p.MedianDelayMs(), p.SourceInfo, p.CadenceLabel, p.Hitches, p.Relocks,
            p.ContentSize.W, p.ContentSize.H, Enumerable.Range(0, 5).Select(p.StageMean).ToArray(), p.CompareMode, p.UpscalerStatus,
            p.NeuralLine(), p.NoSignal, p.SmoothMotionActive, p.HasNeuralStage, p.NeuralRuntimeMissing);
    }
}

/// <summary>The game-named process could not be started or did not answer.</summary>
public sealed class GameProcessException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Line-based JSON over a named pipe: {"t": type, "d": payload}.</summary>
internal sealed class PipeChannel(Stream stream)
{
    private readonly StreamReader _reader = new(stream, new UTF8Encoding(false));
    private readonly StreamWriter _writer = new(stream, new UTF8Encoding(false)) { AutoFlush = true };
    private readonly object _gate = new();

    public bool Send(string type, object? payload = null)
    {
        var line = new JsonObject { ["t"] = type, ["d"] = JsonSerializer.SerializeToNode(payload, Json.Options) }.ToJsonString();
        try
        {
            lock (_gate) _writer.WriteLine(line);
            return true;
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            return false;
        }
    }

    public (string Type, JsonElement Data)? Receive()
    {
        string? line;
        try { line = _reader.ReadLine(); }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException) { return null; }
        if (line == null) return null;
        using var doc = JsonDocument.Parse(line);
        return (doc.RootElement.GetProperty("t").GetString() ?? "", doc.RootElement.GetProperty("d").Clone());
    }
}

/// <summary>The picture running in the game-named exe; this side only sends commands and mirrors its state.</summary>
public sealed class RemotePipeline : IPipeline
{
    private readonly string _exe;
    private readonly StartRequest _request;
    private readonly string _pipeName = "TretioDLSS_Picture_" + Guid.NewGuid().ToString("N");
    private readonly ManualResetEventSlim _started = new();
    private NamedPipeServerStream? _pipe;
    private PipeChannel? _channel;
    private Process? _process;
    private string? _startError;
    private volatile PictureSnapshot _snap = PictureSnapshot.Empty;
    private volatile bool _closing;

    internal RemotePipeline(string exe, StartRequest request)
    {
        _exe = exe;
        _request = request;
    }

    public PipelineStatus Status { get; private set; } = PipelineStatus.Idle;
    public string? StatusMessage { get; private set; }
    public event Action? StatusChanged;
    public event Action<byte[]>? Preview;
    public string ExeName => Path.GetFileName(_exe);

    public void Start()
    {
        SetStatus(PipelineStatus.Starting, null);
        _pipe = new NamedPipeServerStream(_pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        try
        {
            var psi = new ProcessStartInfo(_exe) { UseShellExecute = false, WorkingDirectory = AppContext.BaseDirectory };
            psi.ArgumentList.Add("--pipeline-host");
            psi.ArgumentList.Add(_pipeName);
            _process = Process.Start(psi) ?? throw new GameProcessException("The game process did not start");
        }
        catch (Exception ex) when (ex is not GameProcessException)
        {
            throw new GameProcessException($"{ExeName} did not start: {ex.Message}", ex);
        }
        Log.Information("Picture runs in {Exe} (pid {Pid})", _exe, _process.Id);
        if (!_pipe.WaitForConnectionAsync().Wait(15000))
        {
            Kill();
            throw new GameProcessException($"{ExeName} did not answer");
        }
        _channel = new PipeChannel(_pipe);
        _channel.Send("start", _request);
        new Thread(ReadLoop) { IsBackground = true, Name = "PictureProcess" }.Start();
        if (!_started.Wait(30000))
        {
            Kill();
            throw new GameProcessException($"{ExeName} did not start the picture");
        }
        if (_startError != null) throw new InvalidOperationException(_startError);
    }

    private void ReadLoop()
    {
        while (_channel?.Receive() is { } msg)
        {
            try
            {
                switch (msg.Type)
                {
                    case "started":
                        _startError = Text(msg.Data, "error");
                        _started.Set();
                        break;
                    case "status":
                        SetStatus((PipelineStatus)msg.Data.GetProperty("status").GetInt32(), Text(msg.Data, "message"));
                        break;
                    case "stats":
                        var s = msg.Data.Deserialize<PictureSnapshot>(Json.Options)!;
                        var c = Counters;
                        Interlocked.Exchange(ref c.SourceFrames, s.SourceFrames);
                        Interlocked.Exchange(ref c.RealFrames, s.RealFrames);
                        Interlocked.Exchange(ref c.QuietReal, s.QuietReal);
                        Interlocked.Exchange(ref c.Generated, s.Generated);
                        Interlocked.Exchange(ref c.Held, s.Held);
                        Interlocked.Exchange(ref c.Dropped, s.Dropped);
                        Interlocked.Exchange(ref c.Late, s.Late);
                        _snap = s;
                        break;
                    case "preview":
                        Preview?.Invoke(Convert.FromBase64String(msg.Data.GetString() ?? ""));
                        break;
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Message from {Exe} not understood: {Type}", ExeName, msg.Type);
            }
        }
        if (_closing) return;
        int? code = null;
        try { if (_process?.WaitForExit(2000) == true) code = _process.ExitCode; } catch { }
        Log.Error("{Exe} ended unexpectedly, exit code {Code}", ExeName, code);
        _startError ??= $"{ExeName} stopped";
        _started.Set();
        SetStatus(PipelineStatus.Faulted, $"{ExeName} stopped" + (code != null ? $" (exit code {code})" : ""));
    }

    // Null fields are left out of the JSON.
    private static string? Text(JsonElement e, string name) => e.TryGetProperty(name, out var v) ? v.GetString() : null;

    private void SetStatus(PipelineStatus status, string? message)
    {
        Status = status;
        StatusMessage = message;
        StatusChanged?.Invoke();
    }

    public void SetPreviewFps(double fps) => _channel?.Send("preview", fps);
    public void SetWindowIcon(string? path) => _channel?.Send("icon", path);
    public void SetWindowTitle(string title) => _channel?.Send("title", title);
    public void SetSharpness(double value) => _channel?.Send("sharpness", value);
    public void SetOverlay(bool visible) => _channel?.Send("overlay", visible);
    public void SetHdrLook(HdrConfig look) => _channel?.Send("hdr", look);
    public void ToggleCompare() => _channel?.Send("compare");

    public bool SetNeural(NeuralConfig cfg)
    {
        _channel?.Send("neural", cfg);
        var s = _snap;
        return s.HasNeuralStage || !(cfg.Enabled && s.NeuralRuntimeMissing);
    }

    private (double Source, double Real, double Output) _rates;
    public (double Source, double Real, double Output) LastRates
    {
        get => _rates;
        set
        {
            _rates = value;
            _channel?.Send("rates", new[] { value.Source, value.Real, value.Output });
        }
    }

    public PipelineCounters Counters { get; } = new();
    public CadenceState CadenceState => _snap.CadenceState;
    public double ClockPeriodMs => _snap.ClockPeriodMs;
    public double MedianDelayMs() => _snap.MedianDelayMs;
    public SourceInfo? SourceInfo => _snap.SourceInfo;
    public string CadenceLabel => _snap.CadenceLabel;
    public long Hitches => _snap.Hitches;
    public long Relocks => _snap.Relocks;
    public (int W, int H) ContentSize => (_snap.ContentW, _snap.ContentH);
    public double StageMean(int stage) => stage < _snap.Stages.Length ? _snap.Stages[stage] : 0;
    public bool CompareMode => _snap.CompareMode;
    public string UpscalerStatus => _snap.UpscalerStatus;
    public string NeuralLine() => _snap.NeuralLine;
    public bool NoSignal => _snap.NoSignal;
    public bool SmoothMotionActive => _snap.SmoothMotion;

    private void Kill()
    {
        try { if (_process is { HasExited: false }) _process.Kill(); } catch { }
    }

    public void Dispose()
    {
        _closing = true;
        _channel?.Send("stop");
        try
        {
            if (_process != null && !_process.WaitForExit(8000))
            {
                Log.Warning("{Exe} did not stop in time", ExeName);
                Kill();
            }
        }
        catch { }
        _pipe?.Dispose();
        _process?.Dispose();
    }
}

/// <summary>The game-named exe's side: runs the picture and answers the app over the pipe.</summary>
public static class PipelineChild
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern bool SetDllDirectory(string path);

    public static int Run(string pipeName)
    {
        // The exe lives outside the install folder, where native libraries are looked up first.
        SetDllDirectory(AppContext.BaseDirectory);
        using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            pipe.Connect(10000);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Picture process could not reach the app");
            return 2;
        }
        var channel = new PipeChannel(pipe);
        PipelineHost? host = null;
        Timer? stats = null;
        try
        {
            while (channel.Receive() is { } msg)
            {
                var d = msg.Data;
                switch (msg.Type)
                {
                    case "start":
                        var req = d.Deserialize<StartRequest>(Json.Options)!;
                        try
                        {
                            Log.Information("Picture for the app: source {Source}, icon {Icon}", req.Settings.Source.Kind, req.Icon ?? "none");
                            VfxRuntime.ConfiguredPath = req.Settings.Advanced.VfxRuntimePath;
                            var cfg = AppHost.BuildConfig(req.Settings, req.Devices, Monitors.Enumerate(), req.Tearing);
                            var h = host = new PipelineHost(cfg);
                            h.StatusChanged += () => channel.Send("status", new { status = (int)h.Status, message = h.StatusMessage });
                            h.Preview += jpeg => channel.Send("preview", Convert.ToBase64String(jpeg));
                            h.SetPreviewFps(req.PreviewFps);
                            h.SetWindowIcon(req.Icon);
                            h.Start();
                            channel.Send("started", new { error = (string?)null });
                            stats = new Timer(_ => channel.Send("stats", PictureSnapshot.Of(h)), null, 0, 250);
                        }
                        catch (Exception ex)
                        {
                            Log.Error(ex, "Picture did not start");
                            channel.Send("started", new { error = ex.Message });
                            return 1;
                        }
                        break;
                    case "preview": host?.SetPreviewFps(d.GetDouble()); break;
                    case "icon": host?.SetWindowIcon(d.ValueKind == JsonValueKind.Null ? null : d.GetString()); break;
                    case "title": host?.SetWindowTitle(d.GetString() ?? ""); break;
                    case "sharpness": host?.SetSharpness(d.GetDouble()); break;
                    case "overlay": host?.SetOverlay(d.GetBoolean()); break;
                    case "hdr": host?.SetHdrLook(d.Deserialize<HdrConfig>(Json.Options)!); break;
                    case "neural": host?.SetNeural(d.Deserialize<NeuralConfig>(Json.Options)!); break;
                    case "compare": host?.ToggleCompare(); break;
                    case "rates":
                        if (host != null) host.LastRates = (d[0].GetDouble(), d[1].GetDouble(), d[2].GetDouble());
                        break;
                    case "stop": return 0;
                }
            }
            Log.Warning("The app closed the pipe; stopping the picture");
            return 0;
        }
        finally
        {
            stats?.Dispose();
            host?.Dispose();
            Log.Information("Picture process done");
            Log.CloseAndFlush();
        }
    }
}
