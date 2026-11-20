using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Xml.Linq;
using Serilog;

namespace TretioDLSS.Streaming;

public sealed record TvInfo(string Id, string Name, string Model, string Manufacturer, string Address, bool WebOs);

/// <summary>Finds TVs on the local network: mDNS (AirPlay, Google Cast) and SSDP with the UPnP description.</summary>
public static class TvDiscovery
{
    private static readonly string[] Targets =
    [
        "urn:lge-com:service:webos-second-screen:1",
        "urn:dial-multiscreen-org:service:dial:1",
        "urn:schemas-upnp-org:device:MediaRenderer:1",
    ];

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(3) };
    private static string CachePath => Path.Combine(App.AppInfo.DataFolder, "tvs.json");

    /// <summary>The TVs from the last scan, so names are known at start before the network answers.</summary>
    public static List<TvInfo> LoadCache()
    {
        try
        {
            if (File.Exists(CachePath)) return System.Text.Json.JsonSerializer.Deserialize<List<TvInfo>>(File.ReadAllText(CachePath)) ?? new();
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "TV cache unreadable");
        }
        return new();
    }

    public static void SaveCache(List<TvInfo> tvs)
    {
        try
        {
            File.WriteAllText(CachePath, System.Text.Json.JsonSerializer.Serialize(tvs));
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "TV cache not written");
        }
    }

    public static async Task<List<TvInfo>> ScanAsync(int waitMs = 2500)
    {
        var mdns = MdnsProbe.ScanAsync(waitMs);
        var ssdp = SsdpAsync(waitMs);
        var all = (await mdns).Concat(await ssdp).ToList();
        var unique = all.GroupBy(t => t.Address).Select(g => g.First()).OrderBy(t => t.Name).ToList();
        Log.Information("TV scan: {Count} found ({Names})", unique.Count, string.Join(", ", unique.Select(t => $"{t.Name} at {t.Address}")));
        return unique;
    }

    private static async Task<List<TvInfo>> SsdpAsync(int waitMs)
    {
        var locations = new Dictionary<string, (string Location, string St)>();
        var sockets = new List<UdpClient>();
        foreach (var local in LanAddresses())
        {
            try
            {
                var udp = new UdpClient(new IPEndPoint(local, 0));
                udp.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, local.GetAddressBytes());
                sockets.Add(udp);
                var ep = new IPEndPoint(IPAddress.Parse("239.255.255.250"), 1900);
                foreach (var st in Targets)
                {
                    var msg = Encoding.ASCII.GetBytes($"M-SEARCH * HTTP/1.1\r\nHOST: 239.255.255.250:1900\r\nMAN: \"ssdp:discover\"\r\nMX: 2\r\nST: {st}\r\n\r\n");
                    await udp.SendAsync(msg, ep);
                }
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "SSDP send on {Local} failed", local);
            }
        }

        var deadline = DateTime.UtcNow.AddMilliseconds(waitMs);
        var receives = sockets.Select(async udp =>
        {
            while (DateTime.UtcNow < deadline)
            {
                var left = deadline - DateTime.UtcNow;
                if (left <= TimeSpan.Zero) break;
                try
                {
                    var r = await udp.ReceiveAsync().WaitAsync(left);
                    string text = Encoding.ASCII.GetString(r.Buffer);
                    string? loc = Header(text, "LOCATION"), st = Header(text, "ST");
                    if (loc == null) continue;
                    lock (locations) locations.TryAdd(r.RemoteEndPoint.Address.ToString(), (loc, st ?? ""));
                }
                catch { break; }
            }
        });
        await Task.WhenAll(receives);
        foreach (var s in sockets) s.Dispose();

        var tvs = new List<TvInfo>();
        foreach (var (address, (location, st)) in locations)
        {
            try
            {
                var xml = XDocument.Parse(await Http.GetStringAsync(location));
                var ns = xml.Root!.GetDefaultNamespace();
                var dev = xml.Root.Element(ns + "device");
                if (dev == null) continue;
                string type = dev.Element(ns + "deviceType")?.Value ?? "";
                string name = dev.Element(ns + "friendlyName")?.Value ?? address;
                string model = dev.Element(ns + "modelName")?.Value ?? "";
                string maker = dev.Element(ns + "manufacturer")?.Value ?? "";
                string udn = dev.Element(ns + "UDN")?.Value ?? address;
                bool webOs = st.Contains("webos", StringComparison.OrdinalIgnoreCase) || model.Contains("webOS", StringComparison.OrdinalIgnoreCase);
                bool looksTv = webOs || type.Contains("MediaRenderer") || type.Contains("dial") || name.Contains("TV", StringComparison.OrdinalIgnoreCase);
                if (!looksTv) continue;
                tvs.Add(new TvInfo(udn, CleanName(name), model, maker, address, webOs));
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "SSDP description at {Location} unreadable", location);
            }
        }
        return tvs.GroupBy(t => t.Address).Select(g => g.OrderByDescending(t => t.WebOs).First()).ToList();
    }

    private static string CleanName(string name) => name.Replace("[LG] ", "").Trim();

    private static string? Header(string text, string name)
    {
        foreach (var line in text.Split("\r\n"))
        {
            int i = line.IndexOf(':');
            if (i > 0 && line[..i].Trim().Equals(name, StringComparison.OrdinalIgnoreCase)) return line[(i + 1)..].Trim();
        }
        return null;
    }

    /// <summary>IPv4 addresses of real LAN adapters: up, with a gateway, not a tunnel or virtual switch.</summary>
    public static IEnumerable<IPAddress> LanAddresses()
    {
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up) continue;
            if (ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel or NetworkInterfaceType.Ppp) continue;
            string d = (ni.Description + " " + ni.Name).ToLowerInvariant();
            if (d.Contains("virtual") || d.Contains("vethernet") || d.Contains("wintun") || d.Contains("wireguard") || d.Contains("tailscale") || d.Contains("mullvad") || d.Contains("vpn")) continue;
            var props = ni.GetIPProperties();
            if (!props.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any))) continue;
            foreach (var a in props.UnicastAddresses)
                if (a.Address.AddressFamily == AddressFamily.InterNetwork) yield return a.Address;
        }
    }
}
