using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace KestrelSpike;

/// <summary>
/// Spike CA. Like the POC but PERSISTS the root CA to disk so it survives restarts
/// (validates "regenerate-on-upgrade is fine, but stable within an install" + lets us
/// trust it once on macOS). Leaf certs are cached in-memory per host.
/// </summary>
public sealed class CertificateAuthority
{
    private readonly X509Certificate2 _ca;
    private readonly ConcurrentDictionary<string, X509Certificate2> _cache = new(StringComparer.OrdinalIgnoreCase);

    public X509Certificate2 RootCertificate => _ca;

    public CertificateAuthority(string? rootPath = null)
    {
        _ca = LoadOrCreateRoot(rootPath);
    }

    public X509Certificate2 GetCertificateForHost(string host) => _cache.GetOrAdd(host, CreateLeafCertificate);

    private static X509Certificate2 LoadOrCreateRoot(string? rootPath)
    {
        if (rootPath is not null && File.Exists(rootPath))
        {
            Console.WriteLine($"[spike] loading persisted root CA from {rootPath}");
            return X509CertificateLoader.LoadPkcs12(File.ReadAllBytes(rootPath), null);
        }

        var ca = CreateRootCertificate();
        if (rootPath is not null)
        {
            File.WriteAllBytes(rootPath, ca.Export(X509ContentType.Pkcs12));
            Console.WriteLine($"[spike] created + persisted root CA at {rootPath}");
            Console.WriteLine($"[spike] trust it on macOS with: sudo security add-trusted-cert -d -r trustRoot -k /Library/Keychains/System.keychain {rootPath}.cer (export the .cer first)");
        }
        return ca;
    }

    private static X509Certificate2 CreateRootCertificate()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=DevProxy Kestrel Spike Root CA, O=DevProxy Kestrel Spike",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);

        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));

        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(10));
    }

    private X509Certificate2 CreateLeafCertificate(string host)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            $"CN={host}",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);

        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, false));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, false)); // serverAuth

        var sanBuilder = new SubjectAlternativeNameBuilder();
        if (System.Net.IPAddress.TryParse(host, out var ip))
        {
            sanBuilder.AddIpAddress(ip);
        }
        else
        {
            sanBuilder.AddDnsName(host);
        }
        request.CertificateExtensions.Add(sanBuilder.Build());
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));

        var serialNumber = new byte[8];
        RandomNumberGenerator.Fill(serialNumber);

        using var leaf = request.Create(
            _ca,
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddYears(1),
            serialNumber);

        using var leafWithKey = leaf.CopyWithPrivateKey(rsa);

        return X509CertificateLoader.LoadPkcs12(leafWithKey.Export(X509ContentType.Pkcs12), null);
    }
}
