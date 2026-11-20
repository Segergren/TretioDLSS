using System.Runtime.InteropServices;

namespace TretioDLSS.Streaming;

/// <summary>TretioStream.dll: ENet, Reed-Solomon, NVENC and DNS-SD for the GameStream host.</summary>
internal static unsafe partial class StreamNative
{
    private const string Dll = "TretioStream.dll";

    public static bool Available => File.Exists(Path.Combine(AppContext.BaseDirectory, Dll));

    [LibraryImport(Dll)] public static partial int ts_enet_init();
    [LibraryImport(Dll)] public static partial IntPtr ts_enet_host_create(ushort port, int peers);
    [LibraryImport(Dll)] public static partial void ts_enet_host_destroy(IntPtr host);
    [LibraryImport(Dll)] public static partial int ts_enet_service(IntPtr host, uint timeoutMs, out IntPtr peer, out uint data, byte* buf, int bufLen, out int len);
    [LibraryImport(Dll)] public static partial int ts_enet_send(IntPtr host, IntPtr peer, byte channel, byte* data, int len, int reliable);
    [LibraryImport(Dll)] public static partial void ts_enet_disconnect(IntPtr host, IntPtr peer);
    [LibraryImport(Dll)] public static partial int ts_enet_peer_address(IntPtr peer, byte* buf, int bufLen);

    [LibraryImport(Dll)] public static partial void ts_rs_init();
    [LibraryImport(Dll)] public static partial IntPtr ts_rs_new(int dataShards, int parityShards);
    [LibraryImport(Dll)] public static partial void ts_rs_release(IntPtr rs);
    [LibraryImport(Dll)] public static partial void ts_rs_set_parity(IntPtr rs, byte* p, int len);
    [LibraryImport(Dll)] public static partial int ts_rs_encode(IntPtr rs, byte** shards, int nrShards, int blockSize);

    [LibraryImport(Dll, StringMarshalling = StringMarshalling.Utf16)]
    public static partial IntPtr ts_mdns_register(string instanceName, string hostName, ushort port, byte* err, int errLen);
    [LibraryImport(Dll)] public static partial void ts_mdns_deregister(IntPtr handle);

    [LibraryImport(Dll)] public static partial IntPtr ts_nvenc_create(IntPtr device, int codec, int width, int height, int fps, int kbps, int ring, byte* err, int errLen);
    [LibraryImport(Dll)] public static partial IntPtr ts_nvenc_texture(IntPtr enc, int slot);
    [LibraryImport(Dll)] public static partial int ts_nvenc_slot_busy(IntPtr enc, int slot);
    [LibraryImport(Dll)] public static partial int ts_nvenc_submit(IntPtr enc, int slot, int forceIdr, ulong frameIndex);
    [LibraryImport(Dll)] public static partial int ts_nvenc_collect(IntPtr enc, int slot, uint timeoutMs, out byte* data, out int len, out int idr);
    [LibraryImport(Dll)] public static partial void ts_nvenc_release(IntPtr enc, int slot);
    [LibraryImport(Dll)] public static partial void ts_nvenc_destroy(IntPtr enc);

    public static string Text(byte* err) => Marshal.PtrToStringAnsi((IntPtr)err) ?? "";

    private static int _init;

    public static void EnsureInit()
    {
        if (Interlocked.Exchange(ref _init, 1) == 1) return;
        ts_enet_init();
        ts_rs_init();
    }
}
