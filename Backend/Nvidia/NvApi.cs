using System.Runtime.InteropServices;
using Serilog;

namespace TretioDLSS.Nvidia;

/// <summary>
/// Minimal NVAPI over nvapi64.dll: function pointers through nvapi_QueryInterface.
/// Every call degrades to a failure result; nothing here throws into callers.
/// </summary>
public sealed class NvApi
{
    private const uint IdInitialize = 0x0150E828;
    private const uint IdCreateSession = 0x0694D52E;
    private const uint IdDestroySession = 0xDAD9CFF8;
    private const uint IdLoadSettings = 0x375DBD6B;
    private const uint IdSaveSettings = 0xFCBC7E14;
    private const uint IdFindApplicationByName = 0xEEE566B2;
    private const uint IdFindProfileByName = 0x7E4A9A0B;
    private const uint IdCreateProfile = 0xCC176068;
    private const uint IdCreateApplication = 0x4347A9DE;
    private const uint IdGetSetting = 0x73BF8338;
    private const uint IdSetSetting = 0x577DD202;
    private const uint IdDeleteProfileSetting = 0xE4A26362;
    private const uint IdGetDriverAndBranchVersion = 0x2926AAAD;
    private const uint IdEnumPhysicalGPUs = 0xE5AC921F;
    private const uint IdGetArchInfo = 0xD8265D24;
    private const uint IdGetErrorMessage = 0x6C2D048C;
    private const uint IdEnumAvailableSettingIds = 0xF020614A;
    private const uint IdEnumProfiles = 0xBC371EE0;
    private const uint IdEnumSettings = 0xAE3039DA;
    private const uint IdGetSettingNameFromId = 0xD61CBE6E;

    public const int NvOk = 0;
    public const int NvSettingNotFound = -160;
    public const int NvProfileNotFound = -163;
    public const int NvExecutableNotFound = -166;

    [DllImport("nvapi64.dll", EntryPoint = "nvapi_QueryInterface", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr QueryInterface(uint id);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Fn0();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int FnOutPtr(out IntPtr p);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int FnPtr(IntPtr p);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int FnFindApp(IntPtr session, IntPtr app, out IntPtr profile, IntPtr appStruct);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int FnFindProfile(IntPtr session, IntPtr name, out IntPtr profile);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int FnCreateProfile(IntPtr session, IntPtr profileInfo, out IntPtr profile);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int FnCreateApp(IntPtr session, IntPtr profile, IntPtr app);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int FnGetSetting(IntPtr session, IntPtr profile, uint id, IntPtr setting);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int FnSetSetting(IntPtr session, IntPtr profile, IntPtr setting);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int FnDeleteSetting(IntPtr session, IntPtr profile, uint id);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int FnDriverVersion(out uint version, IntPtr branch);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int FnEnumGpus([Out] IntPtr[] handles, out uint count);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int FnArch(IntPtr gpu, IntPtr info);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int FnErrorMessage(int status, IntPtr text);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int FnEnumIds([Out] uint[] ids, ref uint count);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int FnNameFromId(uint id, IntPtr name);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int FnEnumProfiles(IntPtr session, uint index, out IntPtr profile);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int FnEnumSettings(IntPtr session, IntPtr profile, uint start, ref uint count, IntPtr settings);

    private static readonly Lazy<NvApi?> _instance = new(Load);
    public static NvApi? Instance => _instance.Value;

    private static NvApi? Load()
    {
        try
        {
            var init = Get<Fn0>(IdInitialize);
            if (init == null) return null;
            int status = init();
            if (status != NvOk)
            {
                Log.Information("NVAPI initialize returned {Status}", status);
                return null;
            }
            return new NvApi();
        }
        catch (DllNotFoundException)
        {
            return null;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "NVAPI unavailable");
            return null;
        }
    }

    private static T? Get<T>(uint id) where T : Delegate
    {
        var p = QueryInterface(id);
        return p == IntPtr.Zero ? null : Marshal.GetDelegateForFunctionPointer<T>(p);
    }

    public string ErrorText(int status)
    {
        var f = Get<FnErrorMessage>(IdGetErrorMessage);
        if (f == null) return status.ToString();
        var buf = Marshal.AllocHGlobal(128);
        try
        {
            f(status, buf);
            return Marshal.PtrToStringAnsi(buf) ?? status.ToString();
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
        }
    }

    public string DriverVersion()
    {
        var f = Get<FnDriverVersion>(IdGetDriverAndBranchVersion);
        if (f == null) return "";
        var branch = Marshal.AllocHGlobal(64);
        try
        {
            if (f(out uint v, branch) != NvOk) return "";
            return $"{v / 100}.{v % 100:00}";
        }
        finally
        {
            Marshal.FreeHGlobal(branch);
        }
    }

    /// <summary>GPU architecture id of the first GPU (0x190 Ada, 0x1A0+ Blackwell), or null.</summary>
    public uint? Architecture()
    {
        var enumGpus = Get<FnEnumGpus>(IdEnumPhysicalGPUs);
        var arch = Get<FnArch>(IdGetArchInfo);
        if (enumGpus == null || arch == null) return null;
        var handles = new IntPtr[64];
        if (enumGpus(handles, out uint count) != NvOk || count == 0) return null;
        // NV_GPU_ARCH_INFO_V2: version, architecture_id, implementation_id, revision_id.
        int size = 16;
        var info = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.WriteInt32(info, 0, size | (2 << 16));
            if (arch(handles[0], info) != NvOk) return null;
            return (uint)Marshal.ReadInt32(info, 4);
        }
        finally
        {
            Marshal.FreeHGlobal(info);
        }
    }

    // ---- driver settings (DRS) ----
    // Struct layouts from nvapi.h. NVDRS_SETTING_V1 grew a 64-bit union member in newer headers
    // (0x3028 bytes, value at 0x2020); older drivers know the 0x3020 layout (value at 0x201C).

    private const int UnicodeStringMax = 2048;
    private const int ProfileSize = 0x1014;       // NVDRS_PROFILE_V1
    private const int ApplicationV4Size = 0x500C; // NVDRS_APPLICATION_V4
    public const int IncompatibleStructVersion = -9;
    public const int ExecutableAlreadyInUse = -167;
    public const int InvalidUserPrivilege = -137;

    private readonly record struct SettingLayout(int Size, int ValueOffset);
    private static readonly SettingLayout[] SettingLayouts = [new(0x3028, 0x2020), new(0x3020, 0x201C)];
    private int _settingLayout;

    public sealed class Session : IDisposable
    {
        public IntPtr Handle { get; }
        public Session(IntPtr handle) => Handle = handle;
        public void Dispose() => Get<FnPtr>(IdDestroySession)?.Invoke(Handle);
    }

    public Session? OpenSession()
    {
        var create = Get<FnOutPtr>(IdCreateSession);
        var load = Get<FnPtr>(IdLoadSettings);
        if (create == null || load == null) return null;
        if (create(out var h) != NvOk) return null;
        if (load(h) != NvOk)
        {
            Get<FnPtr>(IdDestroySession)?.Invoke(h);
            return null;
        }
        return new Session(h);
    }

    public int Save(Session s) => Get<FnPtr>(IdSaveSettings)?.Invoke(s.Handle) ?? -1;

    /// <summary>Profile handle that applies to the exe, or zero.</summary>
    public IntPtr FindProfileForApp(Session s, string exeName)
    {
        var f = Get<FnFindApp>(IdFindApplicationByName);
        if (f == null) return IntPtr.Zero;
        var name = UnicodeBuffer(exeName);
        var app = Marshal.AllocHGlobal(ApplicationV4Size);
        try
        {
            ZeroMemory(app, ApplicationV4Size);
            Marshal.WriteInt32(app, 0, ApplicationV4Size | (4 << 16));
            int st = f(s.Handle, name, out var profile, app);
            return st == NvOk ? profile : IntPtr.Zero;
        }
        finally
        {
            Marshal.FreeHGlobal(app);
            Marshal.FreeHGlobal(name);
        }
    }

    /// <summary>Finds or creates a profile with our name and adds the exe to it.</summary>
    public IntPtr EnsureProfile(Session s, string profileName, string exeName)
    {
        var existing = FindProfileForApp(s, exeName);
        if (existing != IntPtr.Zero) return existing;

        var nameBuf = UnicodeBuffer(profileName);
        try
        {
            var find = Get<FnFindProfile>(IdFindProfileByName);
            IntPtr profile = IntPtr.Zero;
            if (find == null || find(s.Handle, nameBuf, out profile) != NvOk)
            {
                var create = Get<FnCreateProfile>(IdCreateProfile);
                if (create == null) return IntPtr.Zero;
                var info = Marshal.AllocHGlobal(ProfileSize);
                try
                {
                    ZeroMemory(info, ProfileSize);
                    Marshal.WriteInt32(info, 0, ProfileSize | (1 << 16));
                    WriteUnicode(info, 4, profileName);
                    int st = create(s.Handle, info, out profile);
                    if (st != NvOk)
                    {
                        Log.Warning("NVAPI CreateProfile failed: {Err}", ErrorText(st));
                        return IntPtr.Zero;
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(info);
                }
            }

            var createApp = Get<FnCreateApp>(IdCreateApplication);
            if (createApp == null) return IntPtr.Zero;
            var app = Marshal.AllocHGlobal(ApplicationV4Size);
            try
            {
                ZeroMemory(app, ApplicationV4Size);
                Marshal.WriteInt32(app, 0, ApplicationV4Size | (4 << 16));
                // version, isPredefined, appName, userFriendlyName, launcher, fileInFolder, flags, commandLine
                WriteUnicode(app, 8, exeName);
                WriteUnicode(app, 8 + UnicodeStringMax * 2, Path.GetFileNameWithoutExtension(exeName));
                int st = createApp(s.Handle, profile, app);
                if (st != NvOk && st != ExecutableAlreadyInUse)
                    Log.Warning("NVAPI CreateApplication failed: {Err}", ErrorText(st));
            }
            finally
            {
                Marshal.FreeHGlobal(app);
            }
            return profile;
        }
        finally
        {
            Marshal.FreeHGlobal(nameBuf);
        }
    }

    /// <summary>Reads a DWORD setting stored on this profile itself. Null when it is inherited or absent.</summary>
    public uint? GetDword(Session s, IntPtr profile, uint settingId)
    {
        var f = Get<FnGetSetting>(IdGetSetting);
        if (f == null || profile == IntPtr.Zero) return null;
        for (int attempt = 0; attempt < SettingLayouts.Length; attempt++)
        {
            int index = (_settingLayout + attempt) % SettingLayouts.Length;
            var layout = SettingLayouts[index];
            var buf = Marshal.AllocHGlobal(layout.Size);
            try
            {
                ZeroMemory(buf, layout.Size);
                Marshal.WriteInt32(buf, 0, layout.Size | (1 << 16));
                int st = f(s.Handle, profile, settingId, buf);
                if (st == IncompatibleStructVersion) continue;
                _settingLayout = index;
                if (st != NvOk) return null;
                int location = Marshal.ReadInt32(buf, 0x100C); // 0 = this profile
                if (location != 0) return null;
                return (uint)Marshal.ReadInt32(buf, layout.ValueOffset);
            }
            finally
            {
                Marshal.FreeHGlobal(buf);
            }
        }
        return null;
    }

    public int SetDword(Session s, IntPtr profile, uint settingId, uint value)
    {
        var f = Get<FnSetSetting>(IdSetSetting);
        if (f == null || profile == IntPtr.Zero) return -1;
        int st = -1;
        for (int attempt = 0; attempt < SettingLayouts.Length; attempt++)
        {
            int index = (_settingLayout + attempt) % SettingLayouts.Length;
            var layout = SettingLayouts[index];
            var buf = Marshal.AllocHGlobal(layout.Size);
            try
            {
                ZeroMemory(buf, layout.Size);
                Marshal.WriteInt32(buf, 0, layout.Size | (1 << 16));
                Marshal.WriteInt32(buf, 0x1004, (int)settingId);
                Marshal.WriteInt32(buf, 0x1008, 0); // NVDRS_DWORD_TYPE
                Marshal.WriteInt32(buf, layout.ValueOffset, (int)value);
                st = f(s.Handle, profile, buf);
                if (st == IncompatibleStructVersion) continue;
                _settingLayout = index;
                return st;
            }
            finally
            {
                Marshal.FreeHGlobal(buf);
            }
        }
        return st;
    }

    /// <summary>Every setting id the driver knows, for diagnostics.</summary>
    public uint[] AvailableSettingIds()
    {
        var f = Get<FnEnumIds>(IdEnumAvailableSettingIds);
        if (f == null) return [];
        var ids = new uint[8192];
        uint count = (uint)ids.Length;
        return f(ids, ref count) == NvOk ? ids.Take((int)count).ToArray() : [];
    }

    public List<IntPtr> Profiles(Session s)
    {
        var list = new List<IntPtr>();
        var f = Get<FnEnumProfiles>(IdEnumProfiles);
        if (f == null) return list;
        for (uint i = 0; i < 20000 && f(s.Handle, i, out var p) == NvOk; i++) list.Add(p);
        return list;
    }

    /// <summary>DWORD settings stored on a profile itself: id and value.</summary>
    public List<(uint Id, uint Value)> StoredSettings(Session s, IntPtr profile)
    {
        var result = new List<(uint, uint)>();
        var f = Get<FnEnumSettings>(IdEnumSettings);
        if (f == null) return result;
        var layout = SettingLayouts[_settingLayout];
        const int Batch = 64;
        var buf = Marshal.AllocHGlobal(layout.Size * Batch);
        try
        {
            for (uint start = 0; ; start += Batch)
            {
                ZeroMemory(buf, layout.Size * Batch);
                for (int i = 0; i < Batch; i++) Marshal.WriteInt32(buf, i * layout.Size, layout.Size | (1 << 16));
                uint count = Batch;
                int st = f(s.Handle, profile, start, ref count, buf);
                if (st == IncompatibleStructVersion && _settingLayout == 0) { _settingLayout = 1; return StoredSettings(s, profile); }
                if (st != NvOk) break;
                for (int i = 0; i < count; i++)
                {
                    var item = buf + i * layout.Size;
                    if (Marshal.ReadInt32(item, 0x1008) != 0) continue;
                    result.Add(((uint)Marshal.ReadInt32(item, 0x1004), (uint)Marshal.ReadInt32(item, layout.ValueOffset)));
                }
                if (count < Batch) break;
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
        }
        return result;
    }

    public string? SettingName(uint id)
    {
        var f = Get<FnNameFromId>(IdGetSettingNameFromId);
        if (f == null) return null;
        var buf = UnicodeBuffer("");
        try
        {
            return f(id, buf) == NvOk ? Marshal.PtrToStringUni(buf) : null;
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
        }
    }

    public int Delete(Session s, IntPtr profile, uint settingId) =>
        Get<FnDeleteSetting>(IdDeleteProfileSetting)?.Invoke(s.Handle, profile, settingId) ?? -1;

    private static IntPtr UnicodeBuffer(string text)
    {
        var buf = Marshal.AllocHGlobal(UnicodeStringMax * 2);
        ZeroMemory(buf, UnicodeStringMax * 2);
        WriteUnicode(buf, 0, text);
        return buf;
    }

    private static void WriteUnicode(IntPtr buf, int offset, string text)
    {
        var chars = text.ToCharArray();
        Marshal.Copy(chars, 0, buf + offset, Math.Min(chars.Length, UnicodeStringMax - 1));
    }

    private static unsafe void ZeroMemory(IntPtr p, int size) => new Span<byte>((void*)p, size).Clear();
}
