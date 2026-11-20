using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Serilog;
using TretioDLSS.App;
using TretioDLSS.Graphics;
using Vortice.Direct3D11;

namespace TretioDLSS.Engines;

/// <summary>
/// DLSS Frame Generation on captured video, x2 to x4. TretioNgx.dll runs it on a D3D12 device of its own;
/// frames cross through shared textures and a shared fence. Blends until the session is ready.
/// x3 and x4 on RTX 40 rely on TretioNgx patching the runtime; if that is refused, the extra positions blend.
/// </summary>
public sealed class DlssFrameGenerator : IFrameGenerator
{
    internal const string Bridge = "TretioNgx.dll";
    internal const string Runtime = "nvngx_dlssg.dll";

    private readonly GraphicsDevice _g;
    private readonly BlendGenerator _fallback;
    private readonly int _multiplier;
    private bool _warnedShort;
    private volatile DlssSession? _session;
    private Task? _building;
    private int _w, _h;
    private bool _reset = true;
    private IntPtr _lastFed, _pairPrev, _pairCur;
    private long _lastFedAt;
    private int _evaluations, _restarts;
    private readonly List<double> _callMs = new();

    public string Id => "dlss-fg";
    public bool LastWasCopy => false;

    /// <summary>Why the engine cannot run from this install, or null.</summary>
    public static string? Missing()
    {
        string dir = AppContext.BaseDirectory;
        if (!File.Exists(Path.Combine(dir, Bridge))) return $"{Bridge} is missing; run Native/TretioNgx/build.ps1";
        if (!File.Exists(Path.Combine(dir, Runtime))) return $"{Runtime} is missing; run tools/dlss/fetch.ps1";
        return null;
    }

    public static IFrameGenerator? TryCreate(GraphicsDevice g, int multiplier)
    {
        if (!g.IsNvidia)
        {
            Log.Warning("DLSS Frame Generation needs the pipeline on the NVIDIA GPU; it runs on {Adapter}", g.AdapterName);
            return null;
        }
        if (Missing() is string missing)
        {
            Log.Warning("DLSS Frame Generation: {Missing}", missing);
            return null;
        }
        return new DlssFrameGenerator(g, Math.Clamp(multiplier, 2, 4));
    }

    private DlssFrameGenerator(GraphicsDevice g, int multiplier)
    {
        _g = g;
        _multiplier = multiplier;
        _fallback = new BlendGenerator(g);
        Log.Information("DLSS Frame Generation x{Multiplier} with {Runtime} {Version}", multiplier, Runtime,
            System.Diagnostics.FileVersionInfo.GetVersionInfo(Path.Combine(AppContext.BaseDirectory, Runtime)).FileVersion);
    }

    public void Prepare(int width, int height)
    {
        if (width == _w && height == _h && (_session != null || _building != null)) return;
        _w = width;
        _h = height;
        var old = _session;
        _session = null;
        _reset = true;
        _building = Task.Run(() =>
        {
            var started = System.Diagnostics.Stopwatch.StartNew();
            var session = DlssSession.Create(_g, width, height, _multiplier - 1);
            if (session == null)
            {
                Log.Error("DLSS Frame Generation failed to start for {W} x {H}; blending instead", width, height);
                return;
            }
            if (_w == width && _h == height) _session = session;
            else session.Dispose();
            Log.Information("DLSS Frame Generation ready for {W} x {H} in {Ms} ms, {Frames} generated per real frame", width, height, started.ElapsedMilliseconds, session.Frames);
        });
        old?.Dispose();
    }

    public void Generate(RenderTexture previous, RenderTexture current, float t, RenderTexture output)
    {
        if (current.Width != _w || current.Height != _h) Prepare(current.Width, current.Height);
        var session = _session;
        if (session == null || session.Failed || current.Format != Vortice.DXGI.Format.R8G8B8A8_UNorm)
        {
            _fallback.Generate(previous, current, t, output);
            return;
        }
        long start = System.Diagnostics.Stopwatch.GetTimestamp();
        IntPtr prev = previous.Texture.NativePointer, cur = current.Texture.NativePointer;
        long now = Environment.TickCount64;
        // Frames of one pair arrive back to back; the runtime keeps the previous frame, so each real frame goes in once.
        if (!(prev == _pairPrev && cur == _pairCur && now - _lastFedAt < 200))
        {
            if (_reset || prev != _lastFed || now - _lastFedAt > 200)
            {
                if (!_reset) _restarts++;
                session.Feed(_g, previous, true);
                _evaluations++;
            }
            _reset = false;
            session.Feed(_g, current, false);
            _evaluations++;
            _lastFed = cur;
            _lastFedAt = now;
            _pairPrev = prev;
            _pairCur = cur;
            if (session.Failed)
            {
                Log.Error("DLSS Frame Generation stopped after {Count} frames; blending instead", _evaluations);
                _fallback.Generate(previous, current, t, output);
                return;
            }
        }
        int index = Math.Clamp((int)Math.Round(t * _multiplier), 1, _multiplier - 1) - 1;
        if (index >= session.Frames)
        {
            if (!_warnedShort) Log.Warning("DLSS Frame Generation gives {Frames} frame(s) per real frame; position {T:F2} is blended", session.Frames, t);
            _warnedShort = true;
            _fallback.Generate(previous, current, t, output);
            return;
        }
        _g.Context.CopyResource(output.Texture, session.Outputs[index]);
        if (_callMs.Count < 100_000) _callMs.Add(System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds);
    }

    public void Reset() => _reset = true;

    public void Dispose()
    {
        if (_callMs.Count > 0)
        {
            var sorted = _callMs.Order().ToList();
            Log.Information("DLSS Frame Generation: {Count} frames, {Evaluations} evaluations, {Restarts} pair restarts, CPU time per call p50 {P50:F2} ms, p99 {P99:F2} ms",
                sorted.Count, _evaluations, _restarts, sorted[sorted.Count / 2], sorted[(int)(sorted.Count * 0.99)]);
        }
        try { _building?.Wait(10000); } catch { }
        _session?.Dispose();
        _session = null;
        _fallback.Dispose();
    }
}

/// <summary>One native session for one frame size, and the D3D11 views of its shared textures and fence.</summary>
internal sealed unsafe class DlssSession : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Desc
    {
        public uint AdapterLow;
        public int AdapterHigh;
        public uint Width, Height;
        public IntPtr RuntimeDir, DataDir, ProjectId, Log;
        public uint Verbose, Frames;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Shared
    {
        public IntPtr Input, Output0, Output1, Output2, Fence;
        public uint Frames;
    }

    [DllImport(DlssFrameGenerator.Bridge, CallingConvention = CallingConvention.Cdecl)]
    private static extern int TngxCreate(Desc* desc, out IntPtr session, out Shared shared);

    [DllImport(DlssFrameGenerator.Bridge, CallingConvention = CallingConvention.Cdecl)]
    private static extern int TngxEvaluate(IntPtr session, ulong inputReady, int reset, out ulong outputReady);

    [DllImport(DlssFrameGenerator.Bridge, CallingConvention = CallingConvention.Cdecl)]
    private static extern void TngxDestroy(IntPtr session);

    [DllImport("kernel32")]
    private static extern bool CloseHandle(IntPtr handle);

    private readonly IntPtr _native;
    private readonly ID3D11Texture2D _input;
    private readonly ID3D11Fence _fence;
    private readonly ID3D11DeviceContext4 _ctx4;
    private ulong _value;

    public ID3D11Texture2D[] Outputs { get; }
    public int Frames => Outputs.Length;
    public bool Failed { get; private set; }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnLog(int level, byte* message)
    {
        string text = Marshal.PtrToStringUTF8((IntPtr)message) ?? "";
        if (level >= 2) Log.Error("DLSS FG: {Message}", text);
        else if (level == 1) Log.Information("DLSS FG: {Message}", text);
        else Log.Debug("DLSS FG: {Message}", text);
    }

    public static DlssSession? Create(GraphicsDevice g, int w, int h, int frames)
    {
        string dataDir = Path.Combine(AppInfo.DataFolder, "ngx");
        Directory.CreateDirectory(dataDir);
        var luid = g.Adapter.Description1.Luid;
        IntPtr runtimeDir = Marshal.StringToHGlobalUni(AppContext.BaseDirectory.TrimEnd('\\'));
        IntPtr data = Marshal.StringToHGlobalUni(dataDir);
        IntPtr projectId = Marshal.StringToHGlobalAnsi(AppInfo.NgxProjectId);
        try
        {
            var desc = new Desc
            {
                AdapterLow = luid.LowPart,
                AdapterHigh = luid.HighPart,
                Width = (uint)w,
                Height = (uint)h,
                RuntimeDir = runtimeDir,
                DataDir = data,
                ProjectId = projectId,
                Log = (IntPtr)(delegate* unmanaged[Cdecl]<int, byte*, void>)&OnLog,
                Verbose = Environment.GetEnvironmentVariable("TRETIO_DEBUG") == "1" ? 1u : 0u,
                Frames = (uint)frames,
            };
            if (TngxCreate(&desc, out IntPtr native, out var shared) != 0) return null;
            try
            {
                return new DlssSession(g, native, shared);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "DLSS Frame Generation: opening the shared textures failed");
                TngxDestroy(native);
                return null;
            }
            finally
            {
                foreach (var handle in new[] { shared.Input, shared.Output0, shared.Output1, shared.Output2, shared.Fence })
                    if (handle != IntPtr.Zero) CloseHandle(handle);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(runtimeDir);
            Marshal.FreeHGlobal(data);
            Marshal.FreeHGlobal(projectId);
        }
    }

    private DlssSession(GraphicsDevice g, IntPtr native, Shared shared)
    {
        _native = native;
        _input = g.Device1.OpenSharedResource1<ID3D11Texture2D>(shared.Input);
        Outputs = new[] { shared.Output0, shared.Output1, shared.Output2 }.Take((int)shared.Frames)
            .Select(handle => g.Device1.OpenSharedResource1<ID3D11Texture2D>(handle)).ToArray();
        using var dev5 = g.Device.QueryInterface<ID3D11Device5>();
        _fence = dev5.OpenSharedFence<ID3D11Fence>(shared.Fence);
        _ctx4 = g.Context.QueryInterface<ID3D11DeviceContext4>();
        _value = _fence.CompletedValue;
    }

    /// <summary>Hands one real frame to the runtime; the generated frames between it and the one before land in Outputs.</summary>
    public void Feed(GraphicsDevice g, RenderTexture frame, bool reset)
    {
        g.Context.CopyResource(_input, frame.Texture);
        ulong ready = ++_value;
        _ctx4.Signal(_fence, ready);
        g.Context.Flush();
        if (TngxEvaluate(_native, ready, reset ? 1 : 0, out ulong done) != 0) Failed = true;
        _value = done;
        _ctx4.Wait(_fence, done);
    }

    public void Dispose()
    {
        _input.Dispose();
        foreach (var output in Outputs) output.Dispose();
        _fence.Dispose();
        _ctx4.Dispose();
        TngxDestroy(_native);
    }
}
