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
