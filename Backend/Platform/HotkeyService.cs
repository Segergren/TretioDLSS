using System.Runtime.InteropServices;
using Serilog;
using static TretioDLSS.Platform.Native;

namespace TretioDLSS.Platform;

/// <summary>Global hotkeys (RegisterHotKey) on a message-only window with its own thread.</summary>
public sealed class HotkeyService : IDisposable
{
    private const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_SHIFT = 0x4, MOD_WIN = 0x8, MOD_NOREPEAT = 0x4000;
    private static readonly IntPtr HWND_MESSAGE = new(-3);
    private readonly Thread _thread;
    private IntPtr _hwnd;
    private readonly ManualResetEventSlim _ready = new();
    private readonly Dictionary<int, Action> _actions = new();
    private readonly Queue<Action> _work = new();
    private int _nextId = 1;
    private static WndProc? _proc;
    private static HotkeyService? _instance;

    public HotkeyService()
    {
        _instance = this;
        _thread = new Thread(Run) { IsBackground = true, Name = "Hotkeys" };
        _thread.Start();
        _ready.Wait(3000);
    }

    private void Run()
    {
        _proc = Proc;
        var wc = new WNDCLASSEX
        {
            cbSize = Marshal.SizeOf<WNDCLASSEX>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_proc),
            hInstance = GetModuleHandle(null),
            lpszClassName = "TretioDLSSHotkeys",
        };
        RegisterClassEx(ref wc);
        _hwnd = CreateWindowEx(0, "TretioDLSSHotkeys", "", 0, 0, 0, 0, 0, HWND_MESSAGE, IntPtr.Zero, wc.hInstance, IntPtr.Zero);
        _ready.Set();
        while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }
    }

    private static IntPtr Proc(IntPtr hwnd, uint msg, IntPtr wp, IntPtr lp)
    {
        var self = _instance;
        if (self != null)
        {
            if (msg == WM_HOTKEY && self._actions.TryGetValue(wp.ToInt32(), out var action))
            {
                try { action(); } catch (Exception ex) { Log.Warning(ex, "Hotkey action failed"); }
                return IntPtr.Zero;
            }
            if (msg == WM_USER + 7)
            {
                lock (self._work)
                    while (self._work.Count > 0) self._work.Dequeue()();
                return IntPtr.Zero;
            }
        }
        return DefWindowProc(hwnd, msg, wp, lp);
    }

    private void OnThread(Action a)
    {
        lock (_work) _work.Enqueue(a);
        PostMessage(_hwnd, WM_USER + 7, IntPtr.Zero, IntPtr.Zero);
    }

    /// <summary>Parses "Ctrl+Alt+F10" style text into modifiers and a virtual key.</summary>
    public static bool TryParse(string text, out uint modifiers, out uint vk)
    {
        modifiers = 0;
        vk = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        foreach (var raw in text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string p = raw.ToUpperInvariant();
            switch (p)
            {
                case "CTRL" or "CONTROL": modifiers |= MOD_CONTROL; break;
                case "ALT": modifiers |= MOD_ALT; break;
                case "SHIFT": modifiers |= MOD_SHIFT; break;
                case "WIN" or "WINDOWS": modifiers |= MOD_WIN; break;
                default:
                    if (p.Length >= 2 && p[0] == 'F' && int.TryParse(p[1..], out int f) && f is >= 1 and <= 24) vk = (uint)(0x70 + f - 1);
                    else if (p.Length == 1 && char.IsLetterOrDigit(p[0])) vk = p[0];
                    else if (p is "HOME") vk = 0x24;
                    else if (p is "END") vk = 0x23;
                    else if (p is "INSERT" or "INS") vk = 0x2D;
                    else if (p is "PAUSE") vk = 0x13;
                    else if (p is "SCROLLLOCK") vk = 0x91;
                    else return false;
                    break;
            }
        }
        return vk != 0;
    }

    public void Register(string text, Action action)
    {
        if (!TryParse(text, out uint mods, out uint vk))
        {
            Log.Warning("Hotkey \"{Text}\" not understood", text);
            return;
        }
        int id = _nextId++;
        OnThread(() =>
        {
            if (RegisterHotKey(_hwnd, id, mods | MOD_NOREPEAT, vk)) _actions[id] = action;
            else Log.Warning("Hotkey {Text} is taken by another app", text);
        });
    }

    public void Clear() => OnThread(() =>
    {
        foreach (var id in _actions.Keys) UnregisterHotKey(_hwnd, id);
        _actions.Clear();
    });

    public void Dispose()
    {
        Clear();
        PostMessage(_hwnd, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        PostThreadMessage(0, 0, IntPtr.Zero, IntPtr.Zero);
    }
}
