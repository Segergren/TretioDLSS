using Microsoft.Win32;

namespace TretioDLSS.Platform;

public sealed record PrivacyState(bool CameraAllowed, bool MicrophoneAllowed);

/// <summary>Windows privacy switches that also gate capture cards (camera and microphone).</summary>
public static class Privacy
{
    private const string Base = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore";

    public static PrivacyState Check() => Evaluate(Read("webcam"), Read("microphone"));

    /// <summary>Values are "Allow" or "Deny" per scope; desktop apps need the global and the NonPackaged switch.</summary>
    internal static PrivacyState Evaluate((string? Global, string? Desktop, string? Machine) camera, (string? Global, string? Desktop, string? Machine) mic) =>
        new(Allowed(camera), Allowed(mic));

    private static bool Allowed((string? Global, string? Desktop, string? Machine) v) =>
        !string.Equals(v.Machine, "Deny", StringComparison.OrdinalIgnoreCase)
        && !string.Equals(v.Global, "Deny", StringComparison.OrdinalIgnoreCase)
        && !string.Equals(v.Desktop, "Deny", StringComparison.OrdinalIgnoreCase);

    private static (string? Global, string? Desktop, string? Machine) Read(string capability)
    {
        try
        {
            using var user = Registry.CurrentUser.OpenSubKey($@"{Base}\{capability}");
            using var desktop = Registry.CurrentUser.OpenSubKey($@"{Base}\{capability}\NonPackaged");
            using var machine = Registry.LocalMachine.OpenSubKey($@"{Base}\{capability}");
            return (user?.GetValue("Value") as string, desktop?.GetValue("Value") as string, machine?.GetValue("Value") as string);
        }
        catch
        {
            return (null, null, null);
        }
    }

    public const string CameraSettingsUri = "ms-settings:privacy-webcam";
    public const string MicrophoneSettingsUri = "ms-settings:privacy-microphone";
}
