using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Serilog;
using TretioDLSS.App;
using TretioDLSS.Graphics;
using Vortice.Direct3D11;
using Vortice.Mathematics;

namespace TretioDLSS.Engines;

/// <summary>What the DLSS 5 stage runs; 0 layers is off. Style and intensity apply live, the rest starts a new session.</summary>
public sealed record NeuralConfig(int Layers, int Style, double Intensity, int Preset, bool BeforeUpscale)
{
    public static readonly NeuralConfig Off = new(0, 0, 1, 2, true);
    public static readonly string[] Styles = ["default", "natural", "cinematic"];

    public bool Enabled => Layers > 0;

    /// <summary>From the settings words; an unknown word falls back to the default.</summary>
    public static NeuralConfig From(string engine, int layers, string style, double intensity, string model, string placement) =>
        new(engine == NeuralRuntime.Id ? Math.Clamp(layers, 1, 4) : 0,
            Math.Max(0, Array.IndexOf(Styles, style)),
            Math.Clamp(intensity, 0, 1),
            model == "original" ? 1 : 2,
            placement != "output");
}

/// <summary>One session's shape: frame size, layer count and model preset.</summary>
internal readonly record struct NeuralKey(int Width, int Height, int Layers, int Preset);

/// <summary>
/// DLSS 5 Neural Rendering over each real frame, in place, with 1 to 4 layers. TretioNgx.dll runs it on a D3D12
/// device of its own; frames cross as RGBA16F through shared textures and a shared fence. Frames pass through
/// untouched while a session starts, and after one failed. Sessions start and end one at a time, off the pipeline thread.
/// </summary>
public sealed class NeuralRenderer : IDisposable
{
    private readonly GraphicsDevice _g;
    private readonly ID3D11Buffer _copyCb;
    private volatile NeuralConfig _cfg;
    private NeuralSession? _session;
    private Task<NeuralSession?>? _building;
    private NeuralKey _buildingKey;
    private NeuralKey? _failedKey;
    private Task _chain = Task.CompletedTask;
    private bool _reset = true;
    private long _frames;
    private volatile string _status = "Off";

    public NeuralConfig Config => _cfg;
    public bool BeforeUpscale => _cfg.BeforeUpscale;

    /// <summary>"Off", "Starting", "Running", or why it is not running.</summary>
    public string Status => _status;

    /// <summary>Null when DLSS 5 cannot run in this pipeline; runtimeMissing says a download would change that.</summary>
    public static NeuralRenderer? TryCreate(GraphicsDevice g, NeuralConfig cfg, out bool runtimeMissing)
    {
        runtimeMissing = g.IsNvidia && !File.Exists(NeuralRuntime.RuntimePath);
        string? why = !g.IsNvidia ? $"needs the pipeline on the NVIDIA GPU; it runs on {g.AdapterName}"
            : NeuralRuntime.BridgeMissing() ?? (runtimeMissing ? "the runtime is not downloaded" : null)
            ?? (SafeMode.IsDisabled(NeuralRuntime.Id) ? SafeMode.Message(NeuralRuntime.Id) : null);
        if (why == null) return new NeuralRenderer(g, cfg);
        if (cfg.Enabled) Log.Warning("DLSS 5 unavailable: {Why}", why);
        return null;
    }

    private NeuralRenderer(GraphicsDevice g, NeuralConfig cfg)
    {
        _g = g;
        _cfg = cfg;
        _copyCb = g.CreateConstantBuffer(16);
        g.Update(_copyCb, new Int4(0, 0, 0, 0));
        Log.Information("DLSS 5 stage with {Runtime} {Version}", NeuralRuntime.FileName, FileVersionInfo.GetVersionInfo(NeuralRuntime.RuntimePath).FileVersion);
    }

    /// <summary>Any thread. Layers, model and frame size start a new session at the next frame.</summary>
    public void Configure(NeuralConfig cfg) => _cfg = cfg;

    /// <summary>The next frame starts without history, for example after a scene cut.</summary>
    public void Reset() => _reset = true;

    /// <summary>Starts a session for frames of this size before the first one arrives.</summary>
    public void Prepare(int width, int height)
    {
        var cfg = _cfg;
        if (cfg.Enabled && _session == null && _building == null) Build(new NeuralKey(width, height, cfg.Layers, cfg.Preset));
    }

    /// <summary>Runs the layers over one real frame in place.</summary>
    public void Process(RenderTexture frame)
    {
        var cfg = _cfg;
        Collect(cfg);
        if (!cfg.Enabled)
        {
            Retire();
            _status = "Off";
            return;
        }
        var key = new NeuralKey(frame.Width, frame.Height, cfg.Layers, cfg.Preset);
        if (_session != null && _session.Key != key) Retire();
        if (_session == null)
        {
            if (_building == null && _failedKey != key) Build(key);
            return;
        }
        _session.Run(_g, frame, _copyCb, _reset, cfg.Style, (float)cfg.Intensity);
        _reset = false;
        _frames++;
        if (_session.Failed)
        {
            Log.Error("DLSS 5 stopped after {Frames} frames, frames pass through: {Error}", _frames, NeuralSession.LastError);
            _status = "Stopped: " + NeuralSession.LastError;
            _failedKey = key;
            Retire();
        }
    }

    private void Collect(NeuralConfig cfg)
    {
        if (_building is not { IsCompleted: true } done) return;
        _building = null;
        var built = done.Result;
        if (built == null)
        {
            _failedKey = _buildingKey;
            _status = "Could not start: " + NeuralSession.LastError;
            return;
        }
        if (!cfg.Enabled || built.Key.Layers != cfg.Layers || built.Key.Preset != cfg.Preset)
        {
            Dispose(built);
            return;
        }
        _session = built;
        _reset = true;
        _frames = 0;
        _status = "Running";
    }

    private void Build(NeuralKey key)
    {
        _buildingKey = key;
        _status = "Starting";
        var g = _g;
        var task = _chain.ContinueWith(_ => NeuralSession.Create(g, key), TaskScheduler.Default);
        _building = task;
        _chain = task;
    }

    private void Retire()
    {
        var s = _session;
        _session = null;
        if (s != null) Dispose(s);
    }

    private void Dispose(NeuralSession s) => _chain = _chain.ContinueWith(_ => s.Dispose(), TaskScheduler.Default);

    public void Dispose()
    {
        try { _chain.Wait(20000); } catch { }
        if (_building is { IsCompletedSuccessfully: true, Result: { } built }) built.Dispose();
        _building = null;
        _session?.Dispose();
        _session = null;
        _copyCb.Dispose();
    }
}

/// <summary>One native session for one frame size and layer count, and the D3D11 views of its shared textures and fence.</summary>
internal sealed unsafe class NeuralSession : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Desc
    {
        public uint AdapterLow;
        public int AdapterHigh;
        public uint Width, Height, Layers, Preset;
        public IntPtr RuntimePath, CallerPath, DataDir, ProjectId, Log;
        public uint Verbose;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Shared
    {
        public IntPtr Input, Output, Fence;
    }

    [DllImport(DlssFrameGenerator.Bridge, CallingConvention = CallingConvention.Cdecl)]
    private static extern int TnrCreate(Desc* desc, out IntPtr session, out Shared shared);

    [DllImport(DlssFrameGenerator.Bridge, CallingConvention = CallingConvention.Cdecl)]
    private static extern int TnrEvaluate(IntPtr session, ulong inputReady, int reset, uint style, float intensity, out ulong outputReady);

    [DllImport(DlssFrameGenerator.Bridge, CallingConvention = CallingConvention.Cdecl)]
    private static extern void TnrDestroy(IntPtr session);

    [DllImport("kernel32")]
    private static extern bool CloseHandle(IntPtr handle);

    /// <summary>The last error the native side reported, for the status line.</summary>
    public static volatile string LastError = "see the log";

    private readonly IntPtr _native;
    private readonly ID3D11Texture2D _input, _output;
    private readonly ID3D11RenderTargetView _inputRtv;
    private readonly ID3D11ShaderResourceView _outputSrv;
    private readonly ID3D11Fence _fence;
    private readonly ID3D11DeviceContext4 _ctx4;
    private ulong _value;

    public NeuralKey Key { get; }
    public bool Failed { get; private set; }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnLog(int level, byte* message)
    {
        string text = Marshal.PtrToStringUTF8((IntPtr)message) ?? "";
        if (level >= 2)
        {
            LastError = text;
            Log.Error("DLSS 5: {Message}", text);
        }
        else if (level == 1) Log.Information("DLSS 5: {Message}", text);
        else Log.Debug("DLSS 5: {Message}", text);
    }

    public static NeuralSession? Create(GraphicsDevice g, NeuralKey key)
    {
        var started = Stopwatch.StartNew();
        string dataDir = Path.Combine(AppInfo.DataFolder, "ngx");
        IntPtr runtime = IntPtr.Zero, caller = IntPtr.Zero, data = IntPtr.Zero, project = IntPtr.Zero;
        try
        {
            Directory.CreateDirectory(dataDir);
            var luid = g.Adapter.Description1.Luid;
            runtime = Marshal.StringToHGlobalUni(NeuralRuntime.RuntimePath);
            caller = Marshal.StringToHGlobalUni(NeuralRuntime.CallerPath);
            data = Marshal.StringToHGlobalUni(dataDir);
            project = Marshal.StringToHGlobalAnsi(AppInfo.NgxProjectId);
            var desc = new Desc
            {
                AdapterLow = luid.LowPart,
                AdapterHigh = luid.HighPart,
                Width = (uint)key.Width,
                Height = (uint)key.Height,
                Layers = (uint)key.Layers,
                Preset = (uint)key.Preset,
                RuntimePath = runtime,
                CallerPath = caller,
                DataDir = data,
                ProjectId = project,
                Log = (IntPtr)(delegate* unmanaged[Cdecl]<int, byte*, void>)&OnLog,
                Verbose = Environment.GetEnvironmentVariable("TRETIO_DEBUG") == "1" ? 1u : 0u,
            };
            if (TnrCreate(&desc, out IntPtr native, out var shared) != 0)
            {
                Log.Error("DLSS 5 did not start for {W} x {H} with {Layers} layer(s); frames pass through", key.Width, key.Height, key.Layers);
                return null;
            }
            try
            {
                var session = new NeuralSession(g, key, native, shared);
                Log.Information("DLSS 5 ready for {W} x {H} with {Layers} layer(s) in {Ms} ms", key.Width, key.Height, key.Layers, started.ElapsedMilliseconds);
                return session;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "DLSS 5: opening the shared textures failed");
                LastError = "opening the shared textures failed";
                TnrDestroy(native);
                return null;
            }
            finally
            {
                foreach (var handle in new[] { shared.Input, shared.Output, shared.Fence })
                    if (handle != IntPtr.Zero) CloseHandle(handle);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "DLSS 5 did not start");
            LastError = ex.Message;
            return null;
        }
        finally
        {
            foreach (var p in new[] { runtime, caller, data, project })
                if (p != IntPtr.Zero) Marshal.FreeHGlobal(p);
        }
    }

    private NeuralSession(GraphicsDevice g, NeuralKey key, IntPtr native, Shared shared)
    {
        Key = key;
        _native = native;
        _input = g.Device1.OpenSharedResource1<ID3D11Texture2D>(shared.Input);
        _output = g.Device1.OpenSharedResource1<ID3D11Texture2D>(shared.Output);
        _inputRtv = g.Device.CreateRenderTargetView(_input);
        _outputSrv = g.Device.CreateShaderResourceView(_output);
        using var dev5 = g.Device.QueryInterface<ID3D11Device5>();
        _fence = dev5.OpenSharedFence<ID3D11Fence>(shared.Fence);
        _ctx4 = g.Context.QueryInterface<ID3D11DeviceContext4>();
        _value = _fence.CompletedValue;
    }

    /// <summary>Copies the frame in, runs the layers on the D3D12 side and copies the result back over the frame.</summary>
    public void Run(GraphicsDevice g, RenderTexture frame, ID3D11Buffer copyCb, bool reset, int style, float intensity)
    {
        var copy = g.PixelShader("util.hlsl", "PSCopy");
        g.Draw(_inputRtv, copy, frame.Viewport, [frame.Srv], copyCb);
        ulong ready = ++_value;
        _ctx4.Signal(_fence, ready);
        g.Context.Flush();
        if (TnrEvaluate(_native, ready, reset ? 1 : 0, (uint)style, intensity, out ulong done) != 0) Failed = true;
        _value = done;
        _ctx4.Wait(_fence, done);
        if (!Failed) g.Draw(frame.Rtv, copy, frame.Viewport, [_outputSrv], copyCb);
    }

    public void Dispose()
    {
        _inputRtv.Dispose();
        _outputSrv.Dispose();
        _input.Dispose();
        _output.Dispose();
        _fence.Dispose();
        _ctx4.Dispose();
        TnrDestroy(_native);
    }
}
