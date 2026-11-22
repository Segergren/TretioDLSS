using System.Runtime.InteropServices;
using Serilog;
using TretioDLSS.Platform;
using static TretioDLSS.Platform.Native;

namespace TretioDLSS.Output;

/// <summary>
/// Native Win32 window for the picture, with its own message loop thread. Borderless and
/// covering the chosen monitor, or a normal window. For window-capture sources it is topmost
/// and never takes focus, so input keeps going to the captured client.
/// </summary>
public sealed class OutputWindow : IDisposable
{
    private const string ClassName = "TretioDLSSOutput";
    private const string TaskbarId = "TretioDLSS.Output";
    private static WndProc? _proc;
    private static bool _registered;

    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new();
    private IntPtr _hwnd;
    private uint _threadId;
    private long _lastMouseMove;
    private bool _cursorHidden;
    private volatile string? _iconPath;
    private IntPtr _iconBig, _iconSmall;

    public IntPtr Handle => _hwnd;
    public int Width { get; private set; }
    public int Height { get; private set; }
    public bool NoActivate { get; }
    public event Action? CloseRequested;
    public event Action? EscapePressed;

    private static readonly Dictionary<IntPtr, OutputWindow> Windows = new();

    public OutputWindow(MonitorInfo monitor, bool borderless, bool noActivate, string title, string? icon = null)
    {
        NoActivate = noActivate;
        _iconPath = icon;
        _thread = new Thread(() => Run(monitor, borderless, noActivate, title)) { IsBackground = true, Name = "OutputWindow" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        if (!_ready.Wait(5000)) throw new TimeoutException("Output window did not open");
        if (_hwnd == IntPtr.Zero) throw new InvalidOperationException("Output window could not be created");
    }

    private void Run(MonitorInfo m, bool borderless, bool noActivate, string title)
    {
        try
        {
            EnsureClass();
            int ex = 0;
            if (noActivate) ex |= WS_EX_NOACTIVATE | WS_EX_TOPMOST;
            int style;
            int x, y, w, h;
            if (borderless)
            {
                style = WS_POPUP;
                x = m.X; y = m.Y; w = m.Width; h = m.Height;
            }
            else
            {
                style = WS_OVERLAPPEDWINDOW;
                w = Math.Min(1600, m.Width * 3 / 4);
                h = w * 9 / 16 + 40;
                x = m.X + (m.Width - w) / 2;
                y = m.Y + (m.Height - h) / 2;
            }
            _hwnd = CreateWindowEx(ex, ClassName, title, style, x, y, w, h, IntPtr.Zero, IntPtr.Zero, GetModuleHandle(null), IntPtr.Zero);
            if (_hwnd == IntPtr.Zero)
            {
                Log.Error("CreateWindowEx failed: {Err}", Marshal.GetLastWin32Error());
                _ready.Set();
                return;
            }
            lock (Windows) Windows[_hwnd] = this;
            WindowIdentity.Set(_hwnd, TaskbarId, title);
            ApplyIcon(_hwnd);
            _threadId = GetCurrentThreadId();
            int dark = 1;
            DwmSetWindowAttribute(_hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, 4);
            GetClientRect(_hwnd, out var rc);
            Width = rc.Width;
            Height = rc.Height;
            ShowWindow(_hwnd, noActivate ? SW_SHOWNOACTIVATE : SW_SHOW);
            if (noActivate) SetWindowPos(_hwnd, HWND_TOPMOST, x, y, w, h, SWP_NOACTIVATE | SWP_SHOWWINDOW);
            else SetForegroundWindow(_hwnd);
            _lastMouseMove = Environment.TickCount64;
            _ready.Set();

            while (true)
            {
                while (PeekMessage(out var msg, IntPtr.Zero, 0, 0, PM_REMOVE))
                {
                    if (msg.message == WM_QUIT) goto done;
                    TranslateMessage(ref msg);
                    DispatchMessage(ref msg);
                }
                // Cursor hides after a second without movement.
                if (!_cursorHidden && Environment.TickCount64 - _lastMouseMove > 1000)
                {
                    _cursorHidden = true;
                    SetCursor(IntPtr.Zero);
                }
                MsgWaitForMultipleObjects(0, IntPtr.Zero, false, 250, 0x04FF);
            }
        done:
            DestroyIcons();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Output window thread failed");
            _ready.Set();
        }
    }

    [DllImport("user32.dll")]
    private static extern uint MsgWaitForMultipleObjects(uint count, IntPtr handles, bool waitAll, uint ms, uint mask);

    /// <summary>Shows this .ico in the title bar, the taskbar and Alt+Tab; null goes back to the app's icon.</summary>
    public void SetIcon(string? path)
    {
        if (path == _iconPath) return;
        _iconPath = path;
        var h = _hwnd;
        if (h != IntPtr.Zero) PostMessage(h, WM_USER + 2, IntPtr.Zero, IntPtr.Zero);
    }

    private static string AppIcon => Path.Combine(AppContext.BaseDirectory, "Resources", "icon.ico");

    private void ApplyIcon(IntPtr hwnd)
    {
        string? path = _iconPath;
        var (big, small) = path != null ? LoadIcons(hwnd, path) : (IntPtr.Zero, IntPtr.Zero);
        if (big == IntPtr.Zero)
        {
            if (path != null) Log.Warning("Window icon {Path} did not load (error {Err})", path, Marshal.GetLastWin32Error());
            path = null;
            (big, small) = LoadIcons(hwnd, AppIcon);
        }
        SendMessage(hwnd, WM_SETICON, ICON_BIG, big);
        SendMessage(hwnd, WM_SETICON, ICON_SMALL, small);
        // The taskbar reads a button's icon once per ID, so each icon gets its own ID.
        string id = path == null ? TaskbarId : TaskbarId + "." + new string(Path.GetFileNameWithoutExtension(path).Where(char.IsAsciiLetterOrDigit).Take(64).ToArray());
        WindowIdentity.SetGroup(hwnd, id, path);
        DestroyIcons();
        (_iconBig, _iconSmall) = (big, small);
        Log.Information("Output window icon: {Icon}", path ?? "app");
    }

    private static (IntPtr Big, IntPtr Small) LoadIcons(IntPtr hwnd, string path)
    {
        uint dpi = GetDpiForWindow(hwnd);
        if (dpi == 0) dpi = 96;
        IntPtr big = IconFile.Load(path, GetSystemMetricsForDpi(SM_CXICON, dpi));
        IntPtr small = IconFile.Load(path, GetSystemMetricsForDpi(SM_CXSMICON, dpi));
        if (big != IntPtr.Zero && small != IntPtr.Zero) return (big, small);
        IconFile.Destroy(big);
        IconFile.Destroy(small);
        return (IntPtr.Zero, IntPtr.Zero);
    }

    private void DestroyIcons()
    {
        IconFile.Destroy(_iconBig);
        IconFile.Destroy(_iconSmall);
        (_iconBig, _iconSmall) = (IntPtr.Zero, IntPtr.Zero);
    }

    private static void EnsureClass()
    {
        if (_registered) return;
        _proc = StaticProc;
        var wc = new WNDCLASSEX
        {
            cbSize = Marshal.SizeOf<WNDCLASSEX>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_proc),
            hInstance = GetModuleHandle(null),
            hCursor = LoadCursor(IntPtr.Zero, IDC_ARROW),
            hbrBackground = IntPtr.Zero,
            lpszClassName = ClassName,
        };
        RegisterClassEx(ref wc);
        _registered = true;
    }

    private static IntPtr StaticProc(IntPtr hwnd, uint msg, IntPtr wp, IntPtr lp)
    {
        OutputWindow? w;
        lock (Windows) Windows.TryGetValue(hwnd, out w);
        if (w != null) return w.Proc(hwnd, msg, wp, lp);
        return DefWindowProc(hwnd, msg, wp, lp);
    }

    private IntPtr Proc(IntPtr hwnd, uint msg, IntPtr wp, IntPtr lp)
    {
        switch (msg)
        {
            case WM_MOUSEACTIVATE when NoActivate:
                return MA_NOACTIVATE;
            case WM_SIZE:
                Width = (int)(lp.ToInt64() & 0xFFFF);
                Height = (int)((lp.ToInt64() >> 16) & 0xFFFF);
                return IntPtr.Zero;
            case WM_MOUSEMOVE:
                _lastMouseMove = Environment.TickCount64;
                if (_cursorHidden)
                {
                    _cursorHidden = false;
                    SetCursor(LoadCursor(IntPtr.Zero, IDC_ARROW));
                }
                break;
            case WM_SETCURSOR when (lp.ToInt64() & 0xFFFF) == HTCLIENT:
                SetCursor(_cursorHidden ? IntPtr.Zero : LoadCursor(IntPtr.Zero, IDC_ARROW));
                return 1;
            case WM_KEYDOWN when wp.ToInt32() == VK_ESCAPE:
                EscapePressed?.Invoke();
                return IntPtr.Zero;
            case WM_CLOSE:
                CloseRequested?.Invoke();
                return IntPtr.Zero;
            case WM_USER + 1:
                WindowIdentity.Clear(hwnd);
                DestroyWindow(hwnd);
                return IntPtr.Zero;
            case WM_USER + 2:
                ApplyIcon(hwnd);
                return IntPtr.Zero;
            case WM_DESTROY:
                lock (Windows) Windows.Remove(hwnd);
                PostQuitMessage(0);
                return IntPtr.Zero;
        }
        return DefWindowProc(hwnd, msg, wp, lp);
    }

    public void Dispose()
    {
        if (_hwnd != IntPtr.Zero)
        {
            var h = _hwnd;
            _hwnd = IntPtr.Zero;
            PostMessage(h, WM_USER + 1, IntPtr.Zero, IntPtr.Zero);
            if (!_thread.Join(2000)) PostThreadMessage(_threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
            lock (Windows) Windows.Remove(h);
        }
        _ready.Dispose();
    }
}
