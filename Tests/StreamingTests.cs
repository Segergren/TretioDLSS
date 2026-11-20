using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using TretioDLSS.Streaming;
using Xunit;

namespace TretioDLSS.Tests;

public class StreamingTests
{
    [Fact]
    public void PortsFollowSunshineOffsets()
    {
        var p = new GameStreamPorts(47989);
        Assert.Equal(47984, p.Https);
        Assert.Equal(47998, p.Video);
        Assert.Equal(47999, p.Control);
        Assert.Equal(48000, p.Audio);
        Assert.Equal(48010, p.Rtsp);
    }

    [Fact]
    public void OpusPacketsHaveOneSizeForAudioFec()
    {
        var sizes = new List<int>();
        var streamer = new AudioStreamer(5, true, packet => sizes.Add(packet.Length));
        var pcm = new float[480 * 2];
        for (int block = 0; block < 40; block++)
        {
            for (int i = 0; i < 480; i++) pcm[i * 2] = pcm[i * 2 + 1] = (float)(0.3 * Math.Sin((block * 480 + i) * 0.05));
            streamer.Write(pcm, 48000);
        }
        Assert.Equal(80, sizes.Count);
        Assert.Single(sizes.Distinct());
    }

    [Fact]
    public void SetupPicksTheSharpestFormatUsbCarries()
    {
        // The Guermok card's Media Foundation list (subset): 4K60 NV12 and 4K30 YUY2 are offered but too much for USB 3.
        var guermok = new List<TretioDLSS.Capture.CaptureFormat>
        {
            new(3840, 2160, 60, "NV12"), new(3840, 2160, 60, "MJPG"), new(3840, 2160, 30, "NV12"), new(3840, 2160, 30, "YUY2"),
            new(2560, 1440, 120, "NV12"), new(2560, 1440, 60, "NV12"), new(2560, 1440, 30, "YUY2"),
            new(1920, 1080, 120, "NV12"), new(1920, 1080, 60, "YUY2"), new(4096, 2160, 30, "NV12"),
        };
        Assert.Equal("3840x2160@30:NV12", TretioDLSS.Capture.FormatPicker.PickForRate(guermok, 30)!.Key);
        Assert.Equal("2560x1440@60:NV12", TretioDLSS.Capture.FormatPicker.PickForRate(guermok, 60)!.Key);
        Assert.Equal("1920x1080@120:NV12", TretioDLSS.Capture.FormatPicker.PickForRate(guermok, 120)!.Key);
    }

    [Fact]
    public void CoverIsAPortraitPng()
    {
        var png = AppCover.Bytes;
        File.WriteAllBytes(Path.Combine(Path.GetTempPath(), "tretio-cover.png"), png);
        using var img = System.Drawing.Image.FromStream(new MemoryStream(png));
        Assert.Equal(600, img.Width);
        Assert.Equal(800, img.Height);
    }

    [Fact]
    public void CertificateSignatureIsTheSignatureValue()
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest("CN=test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        var sig = HostIdentity.Signature(cert);
        Assert.Equal(256, sig.Length);
        Assert.True(cert.RawData.AsSpan().EndsWith(sig));
    }
}
