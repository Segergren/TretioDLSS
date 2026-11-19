using System.Diagnostics;
using System.Text;
using static TretioDLSS.Platform.Native;

namespace TretioDLSS.Platform;

public sealed record WindowEntry(IntPtr Hwnd, string Title, string Process);

public static class WindowList
{
    private const int GWL_EXSTYLE = -20;

    /// <summary>Visible top-level windows with a title, excluding our own process and tool windows.</summary>
    public static List<WindowEntry> Enumerate()
    {
        var list = new List<WindowEntry>();
        int self = Environment.ProcessId;
        var names = new Dictionary<uint, string>();
        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd)) return true;
            int ex = GetWindowLong(hwnd, GWL_EXSTYLE);
            if ((ex & WS_EX_TOOLWINDOW) != 0) return true;
            int len = GetWindowTextLength(hwnd);
            if (len == 0) return true;
            var sb = new StringBuilder(len + 1);
            GetWindowText(hwnd, sb, sb.Capacity);
            string title = sb.ToString();
            GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == self) return true;
            if (!names.TryGetValue(pid, out var proc))
            {
                try { proc = Process.GetProcessById((int)pid).ProcessName + ".exe"; }
                catch { proc = "unknown"; }
                names[pid] = proc;
            }
            if (proc is "explorer.exe" && title is "Program Manager") return true;
            if (proc is "TextInputHost.exe" or "ApplicationFrameHost.exe" or "SystemSettings.exe" && string.IsNullOrWhiteSpace(title)) return true;
            list.Add(new WindowEntry(hwnd, title, proc));
            return true;
        }, IntPtr.Zero);
        return list;
    }

    /// <summary>Finds a window by process name and title, falling back to the process alone.</summary>
    public static WindowEntry? Find(string process, string title)
    {
        var all = Enumerate();
        return all.FirstOrDefault(w => w.Process.Equals(process, StringComparison.OrdinalIgnoreCase) && w.Title == title)
            ?? all.FirstOrDefault(w => w.Process.Equals(process, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(process))
            ?? all.FirstOrDefault(w => w.Title == title && !string.IsNullOrEmpty(title));
    }
}
