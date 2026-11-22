using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Serilog;

namespace TretioDLSS.App;

/// <summary>
/// A copy of the app's launcher named after the game, so the picture runs in a process that other programs
/// see as the game. The copy loads TretioDLSS.dll from the install folder and carries the game's name and icon.
/// </summary>
public static class GameExe
{
    private const int PlaceholderSize = 1024;

    /// <summary>The exe path for a game: Discord's own exe path when it lists one, else the game's name.</summary>
    public static string RelativePath(string game, string? discordExe)
    {
        var parts = (discordExe ?? "").Replace('/', '\\').Split('\\', StringSplitOptions.RemoveEmptyEntries)
            .Select(Clean).Where(p => p.Length > 0 && p != "..").ToList();
        if (parts.Count > 0 && parts[^1].EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return Path.Combine([.. parts]);
        string name = Clean(game);
        return (name.Length == 0 ? "Game" : name) + ".exe";
    }

    private static string Clean(string s) =>
        new string(s.Where(c => !Path.GetInvalidFileNameChars().Contains(c)).ToArray()).Trim().TrimEnd('.').TrimStart('>');

    private static string Root
    {
        get
        {
            string local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppInfo.Name, "games");
            // The launcher finds the dll by a path relative to itself, which needs the same drive.
            return string.Equals(Path.GetPathRoot(local), Path.GetPathRoot(AppContext.BaseDirectory), StringComparison.OrdinalIgnoreCase)
                ? local : Path.Combine(AppContext.BaseDirectory, "games");
        }
    }

    /// <summary>Writes the game's exe if it is missing or out of date and returns its path.</summary>
    public static string Prepare(string relativePath, string displayName, string? icoPath)
    {
        string source = Path.Combine(AppContext.BaseDirectory, AppInfo.ExeName);
        string dll = Path.Combine(AppContext.BaseDirectory, AppInfo.Name + ".dll");
        string target = Path.Combine(Root, relativePath);
        string dir = Path.GetDirectoryName(target)!;
        var src = new FileInfo(source);
        string stamp = $"{src.LastWriteTimeUtc.Ticks}|{src.Length}|{dll}|{displayName}|{icoPath}|{AppInfo.Version}";
        string stampFile = target + ".stamp";
        if (File.Exists(target) && File.Exists(stampFile) && File.ReadAllText(stampFile) == stamp) return target;

        Directory.CreateDirectory(dir);
        byte[] exe = File.ReadAllBytes(source);
        PatchDllPath(exe, Path.GetRelativePath(dir, dll));
        string temp = Path.Combine(dir, Guid.NewGuid().ToString("N") + ".tmp");
        File.WriteAllBytes(temp, exe);
        try
        {
            UpdateResources(temp, displayName, Path.GetFileName(target), icoPath);
            File.Move(temp, target, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
        File.WriteAllText(stampFile, stamp);
        Log.Information("Game exe {Path} written for {Game}", target, displayName);
        return target;
    }

    /// <summary>The launcher stores its dll path in a 1024-byte slot; this one points it at the install folder.</summary>
    internal static void PatchDllPath(byte[] exe, string dllPath)
    {
        byte[] needle = Encoding.UTF8.GetBytes(AppInfo.Name + ".dll\0");
        int at = exe.AsSpan().IndexOf(needle);
        if (at < 0 || exe.AsSpan(at + 1).IndexOf(needle) >= 0) throw new InvalidOperationException("The launcher's dll path was not found once");
        byte[] path = Encoding.UTF8.GetBytes(dllPath);
        if (path.Length >= PlaceholderSize) throw new InvalidOperationException("The install path is too long");
        if (exe.AsSpan(at + needle.Length - 1, PlaceholderSize - needle.Length + 1).ContainsAnyExcept((byte)0))
            throw new InvalidOperationException("The launcher's dll path slot is not empty");
        exe.AsSpan(at, PlaceholderSize).Clear();
        path.CopyTo(exe, at);
    }

    private static readonly IntPtr RtIcon = 3, RtGroupIcon = 14, RtVersion = 16;

    private static void UpdateResources(string file, string displayName, string exeName, string? icoPath)
    {
        var old = new List<(IntPtr Type, IntPtr Name, ushort Lang)>();
        var types = icoPath != null ? new[] { RtIcon, RtGroupIcon, RtVersion } : [RtVersion];
        IntPtr module = LoadLibraryEx(file, IntPtr.Zero, 0x2 | 0x20);
        if (module == IntPtr.Zero) throw new Win32Exception();
        try
        {
            foreach (var type in types)
                EnumResourceNames(module, type, (_, t, name, _) =>
                {
                    // Only numeric ids: the launcher has no named resources of these types.
                    if ((ulong)name > 0xFFFF) return true;
                    EnumResourceLanguages(module, t, name, (_, _, _, lang, _) => { old.Add((t, name, lang)); return true; }, IntPtr.Zero);
                    return true;
                }, IntPtr.Zero);
        }
        finally { FreeLibrary(module); }

        IntPtr h = BeginUpdateResource(file, false);
        if (h == IntPtr.Zero) throw new Win32Exception();
        bool ok = false;
        try
        {
            foreach (var (t, name, lang) in old) Update(h, t, name, lang, null);
            if (icoPath != null) AddIcon(h, File.ReadAllBytes(icoPath));
            Update(h, RtVersion, 1, 0x0409, VersionInfo(displayName, exeName));
            ok = true;
        }
        finally
        {
            if (!EndUpdateResource(h, !ok) && ok) throw new Win32Exception();
        }
    }

    private static void AddIcon(IntPtr h, byte[] ico)
    {
        int count = BitConverter.ToUInt16(ico, 4);
        using var group = new MemoryStream();
        using var w = new BinaryWriter(group);
        w.Write((ushort)0);
        w.Write((ushort)1);
        w.Write((ushort)count);
        for (int i = 0; i < count; i++)
        {
            int e = 6 + 16 * i;
            int size = BitConverter.ToInt32(ico, e + 8), offset = BitConverter.ToInt32(ico, e + 12);
            Update(h, RtIcon, i + 1, 0x0409, ico.AsSpan(offset, size).ToArray());
            w.Write(ico, e, 12);
            w.Write((ushort)(i + 1));
        }
        w.Flush();
        Update(h, RtGroupIcon, 32512, 0x0409, group.ToArray());
    }

    private static void Update(IntPtr h, IntPtr type, IntPtr name, ushort lang, byte[]? data)
    {
        if (!UpdateResource(h, type, name, lang, data, data?.Length ?? 0)) throw new Win32Exception();
    }

    /// <summary>A VS_VERSIONINFO block whose description and product name are the game's.</summary>
    internal static byte[] VersionInfo(string displayName, string exeName)
    {
        var v = Version.TryParse(AppInfo.Version, out var parsed) ? parsed : new Version(0, 1, 0);
        uint ms = (uint)(Math.Max(v.Major, 0) << 16 | Math.Max(v.Minor, 0)), ls = (uint)(Math.Max(v.Build, 0) << 16 | Math.Max(v.Revision, 0));
        using var fixedInfo = new MemoryStream();
        using (var w = new BinaryWriter(fixedInfo, Encoding.Unicode, true))
            foreach (uint x in new[] { 0xFEEF04BDu, 0x10000u, ms, ls, ms, ls, 0x3Fu, 0u, 0x40004u, 1u, 0u, 0u, 0u }) w.Write(x);
        string version = $"{v.Major}.{v.Minor}.{Math.Max(v.Build, 0)}.{Math.Max(v.Revision, 0)}";
        var strings = new (string Key, string Value)[]
        {
            ("FileDescription", displayName),
            ("FileVersion", version),
            ("InternalName", Path.GetFileNameWithoutExtension(exeName)),
            ("OriginalFilename", exeName),
            ("ProductName", displayName),
            ("ProductVersion", version),
        };
        var table = Block("040904B0", [], 0, 1, strings.Select(s => Block(s.Key, Encoding.Unicode.GetBytes(s.Value + "\0"), (ushort)(s.Value.Length + 1), 1)).ToArray());
        var stringInfo = Block("StringFileInfo", [], 0, 1, table);
        var varInfo = Block("VarFileInfo", [], 0, 1, Block("Translation", BitConverter.GetBytes(0x04B00409u), 4, 0));
        return Block("VS_VERSION_INFO", fixedInfo.ToArray(), (ushort)fixedInfo.Length, 0, stringInfo, varInfo);
    }

    private static byte[] Block(string key, byte[] value, ushort valueLength, ushort type, params byte[][] children)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write((ushort)0);
        w.Write(valueLength);
        w.Write(type);
        w.Write(Encoding.Unicode.GetBytes(key + "\0"));
        Pad(w);
        w.Write(value);
        foreach (var child in children)
        {
            Pad(w);
            w.Write(child);
        }
        w.Flush();
        var bytes = ms.ToArray();
        BitConverter.GetBytes((ushort)bytes.Length).CopyTo(bytes, 0);
        return bytes;
    }

    private static void Pad(BinaryWriter w)
    {
        while (w.BaseStream.Position % 4 != 0) w.Write((byte)0);
    }

    private delegate bool EnumNamesProc(IntPtr module, IntPtr type, IntPtr name, IntPtr param);
    private delegate bool EnumLangsProc(IntPtr module, IntPtr type, IntPtr name, ushort lang, IntPtr param);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadLibraryEx(string file, IntPtr reserved, uint flags);

    [DllImport("kernel32.dll")]
    private static extern bool FreeLibrary(IntPtr module);

    [DllImport("kernel32.dll")]
    private static extern bool EnumResourceNames(IntPtr module, IntPtr type, EnumNamesProc proc, IntPtr param);

    [DllImport("kernel32.dll")]
    private static extern bool EnumResourceLanguages(IntPtr module, IntPtr type, IntPtr name, EnumLangsProc proc, IntPtr param);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr BeginUpdateResource(string file, bool deleteExisting);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool UpdateResource(IntPtr update, IntPtr type, IntPtr name, ushort lang, byte[]? data, int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool EndUpdateResource(IntPtr update, bool discard);
}
