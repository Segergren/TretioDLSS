using System.Security.Cryptography;

namespace TretioDLSS.Streaming;

/// <summary>One Moonlight session from launch to disconnect: keys, handshake identifiers and what ANNOUNCE asked for.</summary>
public sealed class StreamSession
{
    public const int EncControlV2 = 0x01, EncVideo = 0x02, EncAudio = 0x04;

    public required LaunchRequest Launch { get; init; }
    public string PingPayload { get; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(8));
    public uint ConnectData { get; } = BitConverter.ToUInt32(RandomNumberGenerator.GetBytes(4));

    // From ANNOUNCE.
    public int PacketSize { get; set; } = 1392;
    public int MinFecPackets { get; set; }
    public int AudioChannels { get; set; } = 2;
    public int AudioPacketMs { get; set; } = 5;
    public bool AudioHighQuality { get; set; } = true;
    public int EncryptionFlags { get; set; }
    public int MlFeatureFlags { get; set; }
    public StreamFormat? Format { get; set; }

    public bool AudioEncrypted => (EncryptionFlags & EncAudio) != 0;
    public bool ControlV2 => (EncryptionFlags & EncControlV2) != 0;

    public DateTime LastControl { get; set; } = DateTime.UtcNow;
}
