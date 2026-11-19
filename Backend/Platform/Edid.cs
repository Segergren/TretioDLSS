using Microsoft.Win32;

namespace TretioDLSS.Platform;

/// <summary>Variable refresh support from a monitor's EDID: FreeSync and HDMI VRR data blocks, DisplayID Adaptive-Sync, or a wide refresh range.</summary>
public static class Edid
{
    public sealed record VrrInfo(bool Vrr, int MinHz, int MaxHz, string Evidence);

    public static VrrInfo Parse(byte[] edid, bool tv)
    {
        if (edid.Length < 128) return new(false, 0, 0, "no EDID");
        int minHz = 0, maxHz = 0;
        for (int d = 54; d <= 108; d += 18)
        {
            if (edid[d] != 0 || edid[d + 1] != 0 || edid[d + 3] != 0xFD) continue;
            minHz = edid[d + 5] + ((edid[d + 4] & 1) != 0 ? 255 : 0);
            maxHz = edid[d + 6] + ((edid[d + 4] & 2) != 0 ? 255 : 0);
        }
        int extensions = edid[126];
        for (int e = 1; e <= extensions && (e + 1) * 128 <= edid.Length; e++)
        {
            var b = edid.AsSpan(e * 128, 128);
            if (b[0] == 0x02 && FindCtaVrr(b, ref minHz, ref maxHz) is string cta) return new(true, minHz, maxHz, cta);
            if (b[0] == 0x70 && HasDisplayIdAdaptiveSync(b)) return new(true, minHz, maxHz, "DisplayID Adaptive-Sync");
        }
        if (!tv && maxHz - minHz >= 48) return new(true, minHz, maxHz, "refresh range");
        return new(false, minHz, maxHz, minHz > 0 ? "fixed range" : "no range");
    }

    private static string? FindCtaVrr(ReadOnlySpan<byte> b, ref int minHz, ref int maxHz)
    {
        int end = Math.Min((int)b[2], 127);
        for (int i = 4; i < end;)
        {
            int tag = b[i] >> 5, len = b[i] & 31;
            if (i + len >= end + 1) break;
            if (tag == 3 && len >= 3)
            {
                int oui = b[i + 1] | b[i + 2] << 8 | b[i + 3] << 16;
                if (oui == 0x00001A) return "FreeSync data block";
                if (oui == 0xC45DD8 && len >= 9)
                {
                    int vrrMin = b[i + 8] & 0x3F;
                    int vrrMax = (b[i + 8] >> 6) << 8 | b[i + 9];
                    if (vrrMax > 0)
                    {
                        minHz = vrrMin;
                        maxHz = vrrMax;
                        return "HDMI VRR data block";
                    }
                }
            }
            i += len + 1;
        }
        return null;
    }

    private static bool HasDisplayIdAdaptiveSync(ReadOnlySpan<byte> b)
    {
        int len = b[2];
        int end = Math.Min(5 + len, 127);
        for (int i = 5; i + 3 <= end;)
        {
            if (b[i] == 0x2B) return true;
            i += 3 + b[i + 2];
        }
        return false;
    }

    /// <summary>EDID from the registry, from a monitor device path like \?\DISPLAY#SAM7409#5&amp;..&amp;UID4353#{guid}.</summary>
    public static byte[]? Read(string devicePath)
    {
        try
        {
            var parts = devicePath.Split('#');
            if (parts.Length < 3) return null;
            using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Enum\DISPLAY\{parts[1]}\{parts[2]}\Device Parameters");
            return key?.GetValue("EDID") as byte[];
        }
        catch
        {
            return null;
        }
    }
}
