using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Serilog;
using TretioDLSS.App;

namespace TretioDLSS.Streaming;

public sealed class PairedClient
{
    public string Name { get; set; } = "";
    public string CertPem { get; set; } = "";
    public DateTime Paired { get; set; }
}

/// <summary>The host's certificate, unique id and paired Moonlight clients, kept in the data folder.</summary>
public sealed class HostIdentity
{
    private sealed class State
    {
        public string UniqueId { get; set; } = "";
        public List<PairedClient> Clients { get; set; } = new();
    }

    private readonly string _dir = Path.Combine(AppInfo.DataFolder, "gamestream");
    private readonly object _gate = new();
    private State _state = new();

    public string UniqueId => _state.UniqueId;
    public string CertPem { get; private set; } = "";
    public byte[] CertSignature { get; private set; } = [];
    public X509Certificate2 TlsCertificate { get; private set; } = null!;
    private RSA _key = null!;

    public IReadOnlyList<PairedClient> Clients
    {
        get { lock (_gate) return _state.Clients.ToList(); }
    }

    public void Load()
    {
        Directory.CreateDirectory(_dir);
        string statePath = Path.Combine(_dir, "state.json");
        try
        {
            if (File.Exists(statePath)) _state = JsonSerializer.Deserialize<State>(File.ReadAllText(statePath)) ?? new State();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "GameStream state unreadable, starting fresh");
        }
        if (string.IsNullOrEmpty(_state.UniqueId)) _state.UniqueId = Guid.NewGuid().ToString().ToUpperInvariant();

        string certPath = Path.Combine(_dir, "cert.pem"), keyPath = Path.Combine(_dir, "key.pem");
        if (!File.Exists(certPath) || !File.Exists(keyPath))
        {
            using var rsa = RSA.Create(2048);
            var req = new CertificateRequest("CN=TretioDLSS", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            using var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(20));
            File.WriteAllText(certPath, cert.ExportCertificatePem());
            File.WriteAllText(keyPath, rsa.ExportPkcs8PrivateKeyPem());
            Log.Information("GameStream: created the host certificate");
        }
        CertPem = File.ReadAllText(certPath).Replace("\r\n", "\n");
        _key = RSA.Create();
        _key.ImportFromPem(File.ReadAllText(keyPath));
        using var pemCert = X509Certificate2.CreateFromPem(CertPem, File.ReadAllText(keyPath));
        // SChannel cannot use an ephemeral key, so go through PKCS#12.
        TlsCertificate = X509CertificateLoader.LoadPkcs12(pemCert.Export(X509ContentType.Pkcs12), null, X509KeyStorageFlags.Exportable);
        CertSignature = Signature(pemCert);
        Save();
    }

    private void Save()
    {
        lock (_gate) File.WriteAllText(Path.Combine(_dir, "state.json"), JsonSerializer.Serialize(_state, new JsonSerializerOptions { WriteIndented = true }));
    }

    public byte[] Sign(byte[] data) => _key.SignData(data, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

    public void AddClient(string name, string certPem)
    {
        lock (_gate)
        {
            _state.Clients.RemoveAll(c => Normalize(c.CertPem) == Normalize(certPem));
            _state.Clients.Add(new PairedClient { Name = name, CertPem = certPem, Paired = DateTime.UtcNow });
        }
        Save();
    }

    public void RemoveClient(string name)
    {
        lock (_gate) _state.Clients.RemoveAll(c => c.Name == name);
        Save();
    }

    public PairedClient? Find(X509Certificate? cert)
    {
        if (cert == null) return null;
        var raw = cert.GetRawCertData();
        lock (_gate)
        {
            foreach (var c in _state.Clients)
            {
                try
                {
                    using var known = X509Certificate2.CreateFromPem(c.CertPem);
                    if (known.RawData.AsSpan().SequenceEqual(raw)) return c;
                }
                catch { }
            }
        }
        return null;
    }

    private static string Normalize(string pem) => pem.Replace("\r", "").Trim();

    /// <summary>The certificate's signature value, the bytes GameStream pairing hashes.</summary>
    public static byte[] Signature(X509Certificate2 cert)
    {
        var reader = new AsnReader(cert.RawData, AsnEncodingRules.DER).ReadSequence();
        reader.ReadEncodedValue();
        reader.ReadEncodedValue();
        return reader.ReadBitString(out _);
    }
}
