using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using Serilog;

namespace TretioDLSS.Streaming;

/// <summary>
/// Finds TVs by their AirPlay and Google Cast mDNS announcements. Queries go to the multicast group and,
/// because routers often drop multicast to Wi-Fi, straight to every neighbor on the LAN as well.
/// </summary>
public static class MdnsProbe
{
    private static readonly string[] Services = ["_airplay._tcp.local", "_googlecast._tcp.local"];

    public static async Task<List<TvInfo>> ScanAsync(int waitMs = 2000)
    {
        var found = new Dictionary<string, TvInfo>();
        var sockets = new List<UdpClient>();
        var targets = new List<IPAddress> { IPAddress.Parse("224.0.0.251") };
        targets.AddRange(Neighbors());
        foreach (var local in TvDiscovery.LanAddresses())
        {
            try
            {
                var udp = new UdpClient(new IPEndPoint(local, 0));
                udp.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, local.GetAddressBytes());
                sockets.Add(udp);
                foreach (var target in targets)
                    foreach (var svc in Services)
                    {
                        var q = Query(svc);
                        try { await udp.SendAsync(q, new IPEndPoint(target, 5353)); } catch { }
                    }
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "mDNS probe on {Local} failed", local);
            }
        }
        var deadline = DateTime.UtcNow.AddMilliseconds(waitMs);
        await Task.WhenAll(sockets.Select(async udp =>
        {
            while (true)
            {
                var left = deadline - DateTime.UtcNow;
                if (left <= TimeSpan.Zero) break;
                try
                {
                    var r = await udp.ReceiveAsync().WaitAsync(left);
                    var tv = Parse(r.Buffer, r.RemoteEndPoint.Address);
                    if (tv != null) lock (found) found.TryAdd(tv.Address, tv);
                }
                catch { break; }
            }
        }));
        foreach (var s in sockets) s.Dispose();
        return found.Values.ToList();
    }

    private static byte[] Query(string name)
    {
        var b = new List<byte> { 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0 };
        foreach (var label in name.Split('.'))
        {
            b.Add((byte)label.Length);
            b.AddRange(Encoding.ASCII.GetBytes(label));
        }
        b.AddRange([0, 0, 12, 0x80, 1]);
        return b.ToArray();
    }

    private static TvInfo? Parse(byte[] p, IPAddress from)
    {
        try
        {
            int an = p[6] << 8 | p[7], ns = p[8] << 8 | p[9], ar = p[10] << 8 | p[11];
            int qd = p[4] << 8 | p[5];
            int o = 12;
            for (int i = 0; i < qd; i++)
            {
                ReadName(p, ref o);
                o += 4;
            }
            string? instance = null;
            var txt = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < an + ns + ar; i++)
            {
                string owner = ReadName(p, ref o);
                int type = p[o] << 8 | p[o + 1];
                int len = p[o + 8] << 8 | p[o + 9];
                int data = o + 10;
                if (type == 12)
                {
                    int d = data;
                    instance ??= ReadName(p, ref d);
                }
                else if (type == 16)
                {
                    for (int t = data; t < data + len;)
                    {
                        int l = p[t++];
                        string kv = Encoding.UTF8.GetString(p, t, l);
                        t += l;
                        int eq = kv.IndexOf('=');
                        if (eq > 0) txt.TryAdd(kv[..eq], kv[(eq + 1)..]);
                    }
                    instance ??= owner;
                }
                o = data + len;
            }
            if (instance == null) return null;
            string label = instance.Split("._")[0];
            bool cast = instance.Contains("_googlecast");
            string name = cast ? txt.GetValueOrDefault("fn", label) : label;
            string model = txt.GetValueOrDefault(cast ? "md" : "model", "");
            string maker = txt.GetValueOrDefault("manufacturer", txt.GetValueOrDefault("integrator", ""));
            if (model.StartsWith("AppleTV", StringComparison.OrdinalIgnoreCase) || model.StartsWith("AudioAccessory", StringComparison.OrdinalIgnoreCase) || model.StartsWith("Mac", StringComparison.OrdinalIgnoreCase)) return null;
            // Speakers announce the same services: keep devices that mirror a screen (AirPlay) or have video out (Cast).
            if (!cast && !(txt.TryGetValue("features", out var f) && long.TryParse(f.Split(',')[0].Replace("0x", ""), System.Globalization.NumberStyles.HexNumber, null, out var bits) && (bits & 0x80) != 0)) return null;
            if (cast && !(int.TryParse(txt.GetValueOrDefault("ca", "0"), out var ca) && (ca & 1) != 0)) return null;
            bool lg = maker.StartsWith("LG", StringComparison.OrdinalIgnoreCase);
            string display = lg && model.Length > 0 && !name.Contains(model) ? $"LG {model}" : name;
            string id = txt.GetValueOrDefault("deviceid", txt.GetValueOrDefault("id", from.ToString()));
            return new TvInfo("mdns:" + id, display, model, maker, from.ToString(), lg);
        }
        catch
        {
            return null;
        }
    }

    private static string ReadName(byte[] p, ref int o)
    {
        var parts = new List<string>();
        int pos = o;
        bool jumped = false;
        for (int guard = 0; guard < 64; guard++)
        {
            int len = p[pos];
            if (len == 0)
            {
                pos++;
                break;
            }
            if ((len & 0xC0) == 0xC0)
            {
                int target = (len & 0x3F) << 8 | p[pos + 1];
                if (!jumped) o = pos + 2;
                jumped = true;
                pos = target;
                continue;
            }
            parts.Add(Encoding.UTF8.GetString(p, pos + 1, len));
            pos += len + 1;
        }
        if (!jumped) o = pos;
        return string.Join('.', parts);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MibIpNetRow
    {
        public int Index;
        public int PhysAddrLen;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)] public byte[] PhysAddr;
        public uint Addr;
        public int Type;
    }

    [DllImport("iphlpapi.dll")]
    private static extern int GetIpNetTable(IntPtr table, ref int size, bool order);

    /// <summary>IPv4 neighbors in the ARP table that are on a LAN subnet.</summary>
    private static List<IPAddress> Neighbors()
    {
        var result = new List<IPAddress>();
        int size = 0;
        GetIpNetTable(IntPtr.Zero, ref size, false);
        if (size <= 0) return result;
        var buf = Marshal.AllocHGlobal(size);
        try
        {
            if (GetIpNetTable(buf, ref size, false) != 0) return result;
            int count = Marshal.ReadInt32(buf);
            int rowSize = Marshal.SizeOf<MibIpNetRow>();
            var lans = TvDiscovery.LanAddresses().Select(a => a.GetAddressBytes()).ToList();
            for (int i = 0; i < count; i++)
            {
                var row = Marshal.PtrToStructure<MibIpNetRow>(buf + 4 + i * rowSize);
                if (row.Type is not (3 or 4)) continue;
                var a = new IPAddress(row.Addr);
                var b = a.GetAddressBytes();
                if (b[0] >= 224 || b[3] == 255) continue;
                if (lans.Any(l => l[0] == b[0] && l[1] == b[1] && l[2] == b[2])) result.Add(a);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
        }
        return result.Distinct().ToList();
    }
}
