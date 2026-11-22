using System.Runtime.InteropServices;
using Serilog;

namespace TretioDLSS.Platform;

/// <summary>
/// Gives a window its own taskbar identity (AppUserModelID), so it shows as a separate
/// app named after its title instead of grouping under the app's exe.
/// </summary>
public static class WindowIdentity
{
    private static readonly Guid Fmtid = new("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3");
    private const uint PidRelaunchCommand = 2, PidRelaunchIcon = 3, PidRelaunchName = 4, PidId = 5;
    private static readonly Guid IidPropertyStore = new("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99");

    public static void Set(IntPtr hwnd, string id, string name)
    {
        var exe = Environment.ProcessPath ?? "";
        Apply(hwnd, store =>
        {
            SetString(store, PidId, id);
            SetString(store, PidRelaunchCommand, $"\"{exe}\"");
            SetString(store, PidRelaunchIcon, $"{exe},0");
            SetString(store, PidRelaunchName, name);
        });
    }

    public static void SetName(IntPtr hwnd, string name) =>
        Apply(hwnd, store => SetString(store, PidRelaunchName, name));

    /// <summary>Moves the window to the taskbar group <paramref name="id"/>, whose button shows this .ico (the app's icon when null).</summary>
    public static void SetGroup(IntPtr hwnd, string id, string? icoPath) => Apply(hwnd, store =>
    {
        SetString(store, PidRelaunchIcon, $"{icoPath ?? Environment.ProcessPath},0");
        SetString(store, PidId, id);
    });

    public static void Clear(IntPtr hwnd) => Apply(hwnd, store =>
    {
        foreach (var pid in new[] { PidRelaunchName, PidRelaunchIcon, PidRelaunchCommand, PidId })
        {
            var key = new PropertyKey { fmtid = Fmtid, pid = pid };
            var empty = new PropVariant();
            store.SetValue(ref key, ref empty);
        }
    });

    private static void Apply(IntPtr hwnd, Action<IPropertyStore> action)
    {
        if (hwnd == IntPtr.Zero) return;
        try
        {
            var iid = IidPropertyStore;
            if (SHGetPropertyStoreForWindow(hwnd, ref iid, out var store) != 0 || store == null) return;
            try
            {
                action(store);
                store.Commit();
            }
            finally { Marshal.ReleaseComObject(store); }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Window identity could not be set");
        }
    }

    private static void SetString(IPropertyStore store, uint pid, string value)
    {
        var key = new PropertyKey { fmtid = Fmtid, pid = pid };
        var pv = new PropVariant { vt = 31, ptr = Marshal.StringToCoTaskMemUni(value) };
        try { store.SetValue(ref key, ref pv); }
        finally { Marshal.FreeCoTaskMem(pv.ptr); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey
    {
        public Guid fmtid;
        public uint pid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropVariant
    {
        public ushort vt;
        public ushort r1, r2, r3;
        public IntPtr ptr;
        public IntPtr pad;
    }

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        void GetCount(out uint count);
        void GetAt(uint index, out PropertyKey key);
        void GetValue(ref PropertyKey key, out PropVariant value);
        void SetValue(ref PropertyKey key, ref PropVariant value);
        void Commit();
    }

    [DllImport("shell32.dll")]
    private static extern int SHGetPropertyStoreForWindow(IntPtr hwnd, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out IPropertyStore store);
}
