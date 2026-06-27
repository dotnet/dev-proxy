// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace DevProxy.Proxy.Kestrel.Internal;

/// <summary>
/// An in-memory certificate authority. It creates a self-signed root CA once and
/// mints (and caches) a leaf certificate per host on demand so the proxy can
/// terminate TLS and inspect the decrypted traffic.
///
/// <para>
/// Slice-1 scope: ephemeral root + leaves, no disk persistence and no OS trust
/// integration. Phase 5 replaces this with the persistent cache + keychain/root
/// store trust that the existing Titanium engine already provides, regenerating
/// on upgrade (no cross-version PFX compatibility contract required).
/// </para>
/// </summary>
internal sealed class CertificateAuthority : IDisposable
{
    private readonly X509Certificate2 _ca;
    private readonly ConcurrentDictionary<string, X509Certificate2> _cache = new(StringComparer.OrdinalIgnoreCase);

    public CertificateAuthority() => _ca = CreateRootCertificate();

    /// <summary>The root CA certificate (public part) clients must trust to allow interception.</summary>
    public X509Certificate2 RootCertificate => _ca;

    public X509Certificate2 GetCertificateForHost(string host) => _cache.GetOrAdd(host, CreateLeafCertificate);

    private static X509Certificate2 CreateRootCertificate()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=Dev Proxy CA, O=Dev Proxy",
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
            [new Oid("1.3.6.1.5.5.7.3.1")], false)); // serverAuth

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

        // Round-trip through PKCS#12 so the certificate is usable as a server
        // certificate by SslStream on every platform.
        return X509CertificateLoader.LoadPkcs12(leafWithKey.Export(X509ContentType.Pkcs12), null);
    }

    public void Dispose()
    {
        _ca.Dispose();
        foreach (var leaf in _cache.Values)
        {
            leaf.Dispose();
        }
        _cache.Clear();
    }
}
