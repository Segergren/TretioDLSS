using System.Runtime.InteropServices;
using Serilog;
using TretioDLSS.Graphics;
using Vortice.Direct3D11;

namespace TretioDLSS.Engines;

/// <summary>AMFVariantStruct (Variant.h): a type and a 16-byte union, 24 bytes.</summary>
[StructLayout(LayoutKind.Explicit, Size = 24)]
internal struct AmfVariant
{
    [FieldOffset(0)] public int Type;
    [FieldOffset(8)] public long Int64;
    [FieldOffset(8)] public byte Bool;

    public static AmfVariant Of(long v) => new() { Type = 2, Int64 = v };
    public static AmfVariant Of(bool v) => new() { Type = 1, Bool = (byte)(v ? 1 : 0) };
}

/// <summary>
/// AMD Frame Rate Conversion from the AMF runtime in AMD's driver: motion estimation
/// on AMD's own hardware path, DirectX 11, x2 only. The pipeline device must be the AMD GPU.
/// Vtable indexes are from the C interfaces in AMF 1.5.3 (Factory.h, Context.h, Component.h,
/// Surface.h, Plane.h; Windows order of the overloaded property-info methods).
/// </summary>
public sealed unsafe class AmfFrameGenerator : IFrameGenerator
{
    private const int Release = 1, SetProperty = 3;
    private const int ContextTerminate = 13, ContextInitDX11 = 18, ContextSurfaceFromDX11 = 49;
    private const int ComponentInit = 17, ComponentTerminate = 19, ComponentFlush = 21, ComponentSubmit = 22, ComponentQuery = 23;
    private const int SurfaceGetPlaneAt = 25, PlaneGetNative = 4;
    private const int AmfOk = 0, AmfDx11_1 = 111, AmfSurfaceRgba = 5;
    private const int FrcEngineDx11 = 3, FrcOnlyInterpolated = 2, FrcProfileHigh = 1, FrcProfileSuper = 2, FrcSearchNative = 0, FrcSearchPerformance = 1;

    [DllImport("amfrt64", CallingConvention = CallingConvention.Cdecl)]
    private static extern int AMFInit(ulong version, out IntPtr factory);

    [DllImport("amfrt64", CallingConvention = CallingConvention.Cdecl)]
    private static extern int AMFQueryVersion(out ulong version);

    private readonly GraphicsDevice _g;
    private readonly IntPtr _factory, _context, _frc;
    private readonly bool _performance;
    private int _w, _h;
    private ID3D11Texture2D? _inA, _inB;
    private bool _nextIsA = true;
    private IntPtr _lastTexture;
    private long _lastAt;
    private readonly List<double> _callMs = new();

    public string Id => "amd-frc";
    public bool LastWasCopy => false;

    private static IntPtr Fn(IntPtr obj, int index) => (*(IntPtr**)obj)[index];

    public static string? RuntimeVersion()
    {
        try
        {
            if (AMFQueryVersion(out ulong v) != AmfOk) return null;
            return $"{v >> 48}.{(v >> 32) & 0xFFFF}.{(v >> 16) & 0xFFFF}.{v & 0xFFFF}";
        }
        catch (Exception)
        {
            return null;
        }
    }

    public static IFrameGenerator? TryCreate(GraphicsDevice g, string model)
    {
        if (!g.IsAmd)
        {
            Log.Warning("AMD Frame Generation needs the pipeline on an AMD GPU; it runs on {Adapter}", g.AdapterName);
            return null;
        }
        if (RuntimeVersion() is not string version)
        {
            Log.Warning("AMD Frame Generation: the AMF runtime (amfrt64.dll) is not installed");
            return null;
        }
        try
        {
            Log.Information("AMD Frame Generation with AMF runtime {Version}", version);
            return new AmfFrameGenerator(g, model == "performance");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "AMD Frame Generation failed to start");
            return null;
        }
    }

    private AmfFrameGenerator(GraphicsDevice g, bool performance)
    {
        _g = g;
        _performance = performance;
        AMFQueryVersion(out ulong version);
        Check(AMFInit(version, out _factory), "AMFInit");
        IntPtr ctx, frc;
        Check(((delegate* unmanaged<IntPtr, IntPtr*, int>)Fn(_factory, 0))(_factory, &ctx), "CreateContext");
        _context = ctx;
        Check(((delegate* unmanaged<IntPtr, IntPtr, int, int>)Fn(_context, ContextInitDX11))(_context, g.Device.NativePointer, AmfDx11_1), "InitDX11");
        IntPtr id = Marshal.StringToHGlobalUni("AMFFRC");
        try { Check(((delegate* unmanaged<IntPtr, IntPtr, IntPtr, IntPtr*, int>)Fn(_factory, 1))(_factory, _context, id, &frc), "CreateComponent(AMFFRC)"); }
        finally { Marshal.FreeHGlobal(id); }
        _frc = frc;
    }

    private void Set(string name, AmfVariant value)
    {
        IntPtr n = Marshal.StringToHGlobalUni(name);
        try { Check(((delegate* unmanaged<IntPtr, IntPtr, AmfVariant, int>)Fn(_frc, SetProperty))(_frc, n, value), name); }
        finally { Marshal.FreeHGlobal(n); }
    }

    public void Prepare(int width, int height)
    {
        if (_inA != null && width == _w && height == _h) return;
        _inA?.Dispose();
        _inB?.Dispose();
        ((delegate* unmanaged<IntPtr, int>)Fn(_frc, ComponentTerminate))(_frc);
        _w = width;
        _h = height;
        var desc = new Texture2DDescription(Vortice.DXGI.Format.R8G8B8A8_UNorm, (uint)width, (uint)height, 1, 1, BindFlags.ShaderResource | BindFlags.RenderTarget);
        _inA = _g.Device.CreateTexture2D(desc);
        _inB = _g.Device.CreateTexture2D(desc);
        Set("FRCEngineType", AmfVariant.Of(FrcEngineDx11));
        Set("FRCMode", AmfVariant.Of(FrcOnlyInterpolated));
        Set("FRCEnableFallback", AmfVariant.Of(true));
        Set("FRCProfile", AmfVariant.Of(width >= 2560 ? FrcProfileSuper : FrcProfileHigh));
        Set("FRCMVSEARCHMODE", AmfVariant.Of(_performance ? FrcSearchPerformance : FrcSearchNative));
        // The future frame improves quality but costs a whole source frame of delay.
        Set("FRCUseFutureFrame", AmfVariant.Of(false));
        Check(((delegate* unmanaged<IntPtr, int, int, int, int>)Fn(_frc, ComponentInit))(_frc, AmfSurfaceRgba, width, height), "Init");
        _lastTexture = IntPtr.Zero;
        Log.Information("AMD Frame Generation ready for {W} x {H} ({Search} motion search)", width, height, _performance ? "performance" : "native");
    }

    public void Generate(RenderTexture previous, RenderTexture current, float t, RenderTexture output)
    {
        if (_inA == null || current.Width != _w || current.Height != _h) Prepare(current.Width, current.Height);
        long start = System.Diagnostics.Stopwatch.GetTimestamp();
        // FRC is a stream: each new real frame is submitted once and yields the frame before it.
        long now = Environment.TickCount64;
        bool continues = previous.Texture.NativePointer == _lastTexture && now - _lastAt < 200;
        if (!continues)
        {
            ((delegate* unmanaged<IntPtr, int>)Fn(_frc, ComponentFlush))(_frc);
            Submit(previous);
            Drain(null);
        }
        Submit(current);
        _lastTexture = current.Texture.NativePointer;
        _lastAt = now;
        if (!Drain(output))
            _g.Context.CopyResource(output.Texture, current.Texture);
        if (_callMs.Count < 100_000) _callMs.Add(System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds);
    }

    private void Submit(RenderTexture frame)
    {
        var target = _nextIsA ? _inA! : _inB!;
        _nextIsA = !_nextIsA;
        _g.Context.CopyResource(target, frame.Texture);
        IntPtr surface;
        Check(((delegate* unmanaged<IntPtr, IntPtr, IntPtr*, IntPtr, int>)Fn(_context, ContextSurfaceFromDX11))(_context, target.NativePointer, &surface, IntPtr.Zero), "CreateSurfaceFromDX11Native");
        try { Check(((delegate* unmanaged<IntPtr, IntPtr, int>)Fn(_frc, ComponentSubmit))(_frc, surface), "SubmitInput"); }
        finally { ((delegate* unmanaged<IntPtr, int>)Fn(surface, Release))(surface); }
    }

    /// <summary>Takes every waiting output; the last one goes into the output texture. False when there was none.</summary>
    private bool Drain(RenderTexture? output)
    {
        bool got = false;
        for (int i = 0; i < 8; i++)
        {
            IntPtr data;
            int r = ((delegate* unmanaged<IntPtr, IntPtr*, int>)Fn(_frc, ComponentQuery))(_frc, &data);
            if (r != AmfOk || data == IntPtr.Zero) break;
            try
            {
                if (output == null) continue;
                IntPtr plane = ((delegate* unmanaged<IntPtr, nuint, IntPtr>)Fn(data, SurfaceGetPlaneAt))(data, 0);
                IntPtr native = ((delegate* unmanaged<IntPtr, IntPtr>)Fn(plane, PlaneGetNative))(plane);
                Marshal.AddRef(native);
                using var tex = new ID3D11Texture2D(native);
                var desc = tex.Description;
                if (desc.ArraySize > 1 && !_loggedArray)
                {
                    _loggedArray = true;
                    Log.Warning("AMD Frame Generation returns a texture array of {Count}; copying slice 0", desc.ArraySize);
                }
                _g.Context.CopySubresourceRegion(output.Texture, 0, 0, 0, 0, tex, 0, new Vortice.Mathematics.Box(0, 0, 0, Math.Min((int)desc.Width, output.Width), Math.Min((int)desc.Height, output.Height), 1));
                got = true;
            }
            finally
            {
                ((delegate* unmanaged<IntPtr, int>)Fn(data, Release))(data);
            }
        }
        return got;
    }

    private bool _loggedArray;

    public void Reset() => _lastTexture = IntPtr.Zero;

    private static void Check(int result, string what)
    {
        if (result != AmfOk) throw new InvalidOperationException($"AMD Frame Generation: {what} failed with AMF error {result}");
    }

    public void Dispose()
    {
        if (_callMs.Count > 0)
        {
            var sorted = _callMs.Order().ToList();
            Log.Information("AMD Frame Generation: {Count} frames, CPU time per call p50 {P50:F2} ms, p99 {P99:F2} ms, max {Max:F2} ms",
                sorted.Count, sorted[sorted.Count / 2], sorted[(int)(sorted.Count * 0.99)], sorted[^1]);
        }
        if (_frc != IntPtr.Zero)
        {
            ((delegate* unmanaged<IntPtr, int>)Fn(_frc, ComponentTerminate))(_frc);
            ((delegate* unmanaged<IntPtr, int>)Fn(_frc, Release))(_frc);
        }
        if (_context != IntPtr.Zero)
        {
            ((delegate* unmanaged<IntPtr, int>)Fn(_context, ContextTerminate))(_context);
            ((delegate* unmanaged<IntPtr, int>)Fn(_context, Release))(_context);
        }
        _inA?.Dispose();
        _inB?.Dispose();
    }
}
