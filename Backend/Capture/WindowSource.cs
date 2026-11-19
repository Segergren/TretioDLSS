using System.Runtime.InteropServices;
using Serilog;
using TretioDLSS.Graphics;
using TretioDLSS.Pipeline;
using Vortice.Direct3D11;
using Windows.Foundation.Metadata;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;
using static TretioDLSS.Platform.Native;

namespace TretioDLSS.Capture;

/// <summary>A source that needs the pipeline's graphics device before it starts.</summary>
public interface IGpuSource
{
    void Attach(GraphicsDevice device);
}

/// <summary>
/// Captures one window with Windows.Graphics.Capture. The capture worker
/// thread only wraps the frame; the pipeline thread copies the client area and releases it.
/// </summary>
public sealed class WindowSource : IVideoSource, IGpuSource
{
    private readonly IntPtr _hwnd;
    private GraphicsDevice? _g;
    private IDirect3DDevice? _winrtDevice;
    private GraphicsCaptureItem? _item;
    private Direct3D11CaptureFramePool? _pool;
    private GraphicsCaptureSession? _session;
    private IFrameSink? _sink;
    private ulong _seq;
    private Windows.Graphics.SizeInt32 _poolSize;
    private volatile bool _stopped;
    private Thread? _watch;
    private readonly object _gate = new();

    private const string MinimizedMessage = "Waiting for the window: it is minimized. Restore it; it can stay covered.";

    public SourceInfo Info { get; private set; }

    public WindowSource(IntPtr hwnd, string title, string process)
    {
        _hwnd = hwnd;
        GetClientRect(hwnd, out var rc);
        Info = new SourceInfo(title, "window", Math.Max(2, rc.Width & ~1), Math.Max(2, rc.Height & ~1), 60, PixelLayout.Bgra8, false);
    }

    public void Attach(GraphicsDevice device) => _g = device;

    [DllImport("d3d11.dll")]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);

    [DllImport("combase.dll", CharSet = CharSet.Unicode)]
    private static extern int WindowsCreateString(string source, int length, out IntPtr hstring);

    [DllImport("combase.dll")]
    private static extern int WindowsDeleteString(IntPtr hstring);

    [DllImport("combase.dll")]
    private static extern int RoGetActivationFactory(IntPtr classId, ref Guid iid, out IntPtr factory);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out RECT rect, int size);

    [DllImport("user32.dll")]
    private static extern bool ClientToScreen(IntPtr hwnd, ref POINT p);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    private static readonly Guid InteropIid = new("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356");
    private static readonly Guid ItemIid = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");
    private static readonly Guid DxgiAccessIid = new("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1");
    private static readonly Guid Texture2DIid = typeof(ID3D11Texture2D).GUID;

    private static unsafe GraphicsCaptureItem CreateItem(IntPtr hwnd)
    {
        const string cls = "Windows.Graphics.Capture.GraphicsCaptureItem";
        WindowsCreateString(cls, cls.Length, out var hs);
        try
        {
            var iid = InteropIid;
            Marshal.ThrowExceptionForHR(RoGetActivationFactory(hs, ref iid, out var factory));
            try
            {
                var vtbl = *(IntPtr**)factory;
                var createForWindow = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, Guid*, IntPtr*, int>)vtbl[3];
                var itemIid = ItemIid;
                IntPtr result;
                Marshal.ThrowExceptionForHR(createForWindow(factory, hwnd, &itemIid, &result));
                try { return GraphicsCaptureItem.FromAbi(result); }
                finally { Marshal.Release(result); }
            }
            finally
            {
                Marshal.Release(factory);
            }
        }
        finally
        {
            WindowsDeleteString(hs);
        }
    }

    private static IDirect3DDevice CreateWinrtDevice(GraphicsDevice g)
    {
        using var dxgi = g.Device.QueryInterface<Vortice.DXGI.IDXGIDevice>();
        Marshal.ThrowExceptionForHR(CreateDirect3D11DeviceFromDXGIDevice(dxgi.NativePointer, out var ptr));
        try { return MarshalInterface<IDirect3DDevice>.FromAbi(ptr); }
        finally { Marshal.Release(ptr); }
    }

    private static unsafe ID3D11Texture2D TextureOf(IDirect3DSurface surface)
    {
        var abi = MarshalInterface<IDirect3DSurface>.FromManaged(surface);
        try
        {
            var iid = DxgiAccessIid;
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(abi, in iid, out var access));
            try
            {
                var vtbl = *(IntPtr**)access;
                var getInterface = (delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)vtbl[3];
                var tiid = Texture2DIid;
                IntPtr tex;
                Marshal.ThrowExceptionForHR(getInterface(access, &tiid, &tex));
                return new ID3D11Texture2D(tex);
            }
            finally
            {
                Marshal.Release(access);
            }
        }
        finally
        {
            Marshal.Release(abi);
        }
    }

    public void Start(IFrameSink sink)
    {
        if (_g == null) throw new InvalidOperationException("Window capture needs the graphics device");
        if (!IsWindow(_hwnd)) throw new InvalidOperationException("The window is closed");
        _sink = sink;
        _stopped = false;
        // A minimized window has no picture to capture: wait for it instead of failing.
        if (IsIconic(_hwnd))
        {
            Log.Information("\"{Title}\" is minimized; capture starts when it is restored", Info.Name);
            sink.OnSourceEvent(SourceEvent.DeviceLost, MinimizedMessage);
        }
        else
        {
            BeginCapture(sink);
        }
        _watch = new Thread(Watch) { IsBackground = true, Name = "WindowWatch" };
        _watch.Start();
    }

    /// <summary>Follows minimize and restore: waiting while minimized, capturing again once restored.</summary>
    private void Watch()
    {
        bool minimized = IsIconic(_hwnd);
        while (!_stopped)
        {
            Thread.Sleep(200);
            if (_stopped) return;
            var sink = _sink;
            if (sink == null) return;
            if (!IsWindow(_hwnd))
            {
                sink.OnSourceEvent(SourceEvent.DeviceLost, "The captured window was closed");
                return;
            }
            bool now = IsIconic(_hwnd);
            if (now == minimized) continue;
            minimized = now;
            if (now)
            {
                Log.Information("\"{Title}\" was minimized", Info.Name);
                sink.OnSourceEvent(SourceEvent.DeviceLost, MinimizedMessage);
            }
            else if (_session == null)
            {
                try { BeginCapture(sink); }
                catch (Exception ex)
                {
                    Log.Warning(ex, "Window capture did not start after the restore");
                    sink.OnSourceEvent(SourceEvent.Error, "Window capture did not start: " + ex.Message);
                }
            }
            else
            {
                Log.Information("\"{Title}\" was restored", Info.Name);
                sink.OnSourceEvent(SourceEvent.DeviceBack);
            }
        }
    }

    private void BeginCapture(IFrameSink sink)
    {
        lock (_gate)
        {
            if (_stopped) return;
            BeginCaptureCore(sink);
        }
    }

    private void BeginCaptureCore(IFrameSink sink)
    {
        _winrtDevice = CreateWinrtDevice(_g!);
        _item = CreateItem(_hwnd);
        _poolSize = _item.Size;
        _pool = Direct3D11CaptureFramePool.CreateFreeThreaded(_winrtDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 3, _poolSize);
        _pool.FrameArrived += OnFrameArrived;
        _session = _pool.CreateCaptureSession(_item);
        try { _session.IsCursorCaptureEnabled = false; } catch { }
        try
        {
            if (ApiInformation.IsPropertyPresent("Windows.Graphics.Capture.GraphicsCaptureSession", "IsBorderRequired"))
            {
                _ = GraphicsCaptureAccess.RequestAccessAsync(GraphicsCaptureAccessKind.Borderless);
                _session.IsBorderRequired = false;
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Borderless capture not available");
        }
        try
        {
            if (ApiInformation.IsPropertyPresent("Windows.Graphics.Capture.GraphicsCaptureSession", "MinUpdateInterval"))
                _session.MinUpdateInterval = TimeSpan.FromMilliseconds(1);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "MinUpdateInterval not available");
        }
        _item.Closed += (_, _) => sink.OnSourceEvent(SourceEvent.DeviceLost, "The captured window was closed");
        _session.StartCapture();
        sink.OnSourceEvent(SourceEvent.Started);
        Log.Information("Window capture started on \"{Title}\" ({W}x{H})", Info.Name, _poolSize.Width, _poolSize.Height);
    }

    /// <summary>Client area inside the captured frame (which covers the window's visible bounds).</summary>
    private (int X, int Y, int W, int H) ClientCrop(int frameW, int frameH)
    {
        if (DwmGetWindowAttribute(_hwnd, 9 /* DWMWA_EXTENDED_FRAME_BOUNDS */, out var bounds, Marshal.SizeOf<RECT>()) != 0)
            GetWindowRect(_hwnd, out bounds);
        GetClientRect(_hwnd, out var client);
        var origin = new POINT();
        ClientToScreen(_hwnd, ref origin);
        int x = Math.Clamp(origin.X - bounds.Left, 0, frameW - 2);
        int y = Math.Clamp(origin.Y - bounds.Top, 0, frameH - 2);
        int w = Math.Clamp(client.Width, 2, frameW - x) & ~1;
        int h = Math.Clamp(client.Height, 2, frameH - y) & ~1;
        return (x, y, w, h);
    }

    private void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
    {
        if (_stopped) return;
        Direct3D11CaptureFrame? frame = null;
        try
        {
            frame = sender.TryGetNextFrame();
            if (frame == null) return;
            var size = frame.ContentSize;
            // Minimizing and restoring deliver frames of the title-bar stub (about 146 x 28). Resizing the
            // pool to them and back while earlier frames are still in use crashes the process.
            if (IsIconic(_hwnd) || size.Width < 64 || size.Height < 64) return;
            if (size.Width != _poolSize.Width || size.Height != _poolSize.Height)
            {
                _poolSize = size;
                sender.Recreate(_winrtDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 3, size);
            }
            var tex = TextureOf(frame.Surface);
            var crop = ClientCrop(size.Width, size.Height);
            var captured = frame;
            var gf = new GpuFrame(tex, crop.X, crop.Y, crop.W, crop.H, () =>
            {
                tex.Dispose();
                captured.Dispose();
            })
            {
                CaptureQpc = (long)(frame.SystemRelativeTime.Ticks * ((double)Qpc.Frequency / TimeSpan.TicksPerSecond)),
                Sequence = _seq++,
            };
            frame = null;
            if (Info.Width != crop.W || Info.Height != crop.H) Info = Info with { Width = crop.W, Height = crop.H };
            _sink?.OnFrame(gf);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Window capture frame failed");
        }
        finally
        {
            frame?.Dispose();
        }
    }

    public void Stop()
    {
        _stopped = true;
        lock (_gate)
        {
            StopCapture();
        }
        _watch?.Join(1000);
    }

    private void StopCapture()
    {
        try { _session?.Dispose(); } catch { }
        try
        {
            if (_pool != null) _pool.FrameArrived -= OnFrameArrived;
            _pool?.Dispose();
        }
        catch { }
        _session = null;
        _pool = null;
    }

    public void Dispose() => Stop();
}
