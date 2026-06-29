// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using DevProxy.Proxy.Kestrel.Internal;
using Xunit;

namespace DevProxy.Proxy.Kestrel.Tests;

/// <summary>
/// Verifies the persistent certificate authority: a root saved to disk is reused
/// across instances (so existing OS trust is inherited), invalid/expired roots are
/// regenerated, leaves are minted + cached on disk, and filename-unsafe hosts are
/// handled. Each test runs in its own temp directory so nothing touches the real
/// Dev Proxy config folder.
/// </summary>
public sealed class CertificateAuthorityTests : IDisposable
{
    private readonly string _dir;
    private readonly string _rootPath;
    private readonly string _leafDir;

    public CertificateAuthorityTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "devproxy-ca-tests-" + Guid.NewGuid().ToString("N"));
        _ = Directory.CreateDirectory(_dir);
        _rootPath = Path.Combine(_dir, "rootCert.pfx");
        _leafDir = Path.Combine(_dir, "crts");
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_dir))
            {
                Directory.Delete(_dir, recursive: true);
            }
        }
        catch (IOException)
        {
            // best-effort cleanup
        }
    }

    [Fact]
    public void FirstRun_CreatesValidRoot_AndPersistsToDisk()
    {
        using var ca = new CertificateAuthority(_rootPath, _leafDir);

        var root = ca.RootCertificate;
        Assert.Equal("CN=Dev Proxy CA", root.Subject);
        Assert.True(root.HasPrivateKey);
        Assert.True(IsCertificateAuthority(root));
        Assert.True(root.NotAfter > DateTime.Now);
        Assert.True(File.Exists(_rootPath));
    }

    [Fact]
    public void SecondInstance_ReusesPersistedRoot()
    {
        string thumbprint;
        using (var ca1 = new CertificateAuthority(_rootPath, _leafDir))
        {
            thumbprint = ca1.RootCertificate.Thumbprint;
        }

        using var ca2 = new CertificateAuthority(_rootPath, _leafDir);
        Assert.Equal(thumbprint, ca2.RootCertificate.Thumbprint);
    }

    [Fact]
    public void InvalidRootFile_Regenerates()
    {
        File.WriteAllBytes(_rootPath, [0x00, 0x01, 0x02, 0x03]);

        using var ca = new CertificateAuthority(_rootPath, _leafDir);

        Assert.True(IsCertificateAuthority(ca.RootCertificate));
        Assert.True(ca.RootCertificate.NotAfter > DateTime.Now);
        // The garbage file was overwritten with a real PKCS#12.
        using var reloaded = new CertificateAuthority(_rootPath, _leafDir);
        Assert.Equal(ca.RootCertificate.Thumbprint, reloaded.RootCertificate.Thumbprint);
    }

    [Fact]
    public void ExpiredRoot_Regenerates()
    {
        File.WriteAllBytes(_rootPath, CreateRootPfx(NotAfter: DateTimeOffset.UtcNow.AddDays(-1)));

        using var ca = new CertificateAuthority(_rootPath, _leafDir);

        Assert.True(ca.RootCertificate.NotAfter > DateTime.Now);
    }

    [Fact]
    public void NonCaRoot_Regenerates()
    {
        File.WriteAllBytes(_rootPath, CreateNonCaPfx());

        using var ca = new CertificateAuthority(_rootPath, _leafDir);

        Assert.True(IsCertificateAuthority(ca.RootCertificate));
    }

    [Fact]
    public void GetCertificateForHost_MintsLeafSignedByRoot_AndPersists()
    {
        using var ca = new CertificateAuthority(_rootPath, _leafDir);

        var leaf = ca.GetCertificateForHost("example.com");

        Assert.True(leaf.HasPrivateKey);
        Assert.Equal("CN=example.com", leaf.Subject);
        Assert.Equal(ca.RootCertificate.Subject, leaf.Issuer);
        Assert.Contains("example.com", GetSanText(leaf), StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(_leafDir, "example.com.pfx")));
    }

    [Fact]
    public void GetCertificateForHost_SameHost_ReturnsCachedInstance()
    {
        using var ca = new CertificateAuthority(_rootPath, _leafDir);

        var first = ca.GetCertificateForHost("example.com");
        var second = ca.GetCertificateForHost("example.com");

        Assert.Same(first, second);
    }

    [Fact]
    public void SecondInstance_ReusesPersistedLeaf()
    {
        string thumbprint;
        using (var ca1 = new CertificateAuthority(_rootPath, _leafDir))
        {
            thumbprint = ca1.GetCertificateForHost("example.com").Thumbprint;
        }

        using var ca2 = new CertificateAuthority(_rootPath, _leafDir);
        Assert.Equal(thumbprint, ca2.GetCertificateForHost("example.com").Thumbprint);
    }

    [Fact]
    public void FreshRoot_PurgesStaleLeafCache()
    {
        _ = Directory.CreateDirectory(_leafDir);
        var stale = Path.Combine(_leafDir, "stale.pfx");
        File.WriteAllBytes(stale, [0x00]);

        // No root file exists -> a fresh root is created -> the old leaf cache is dropped.
        using var ca = new CertificateAuthority(_rootPath, _leafDir);

        Assert.False(File.Exists(stale));
    }

    [Fact]
    public void GetCertificateForHost_IpLiteral_AddsIpSan()
    {
        using var ca = new CertificateAuthority(_rootPath, _leafDir);

        var leaf = ca.GetCertificateForHost("127.0.0.1");

        Assert.True(leaf.HasPrivateKey);
        Assert.Contains("127.0.0.1", GetSanText(leaf));
    }

    [Fact]
    public void GetCertificateForHost_FilenameUnsafeHost_IsSanitized()
    {
        using var ca = new CertificateAuthority(_rootPath, _leafDir);

        // '/' is filename-invalid on every platform; the leaf must still be minted and
        // the on-disk cache key sanitized rather than throwing.
        var leaf = ca.GetCertificateForHost("a/b");

        Assert.True(leaf.HasPrivateKey);
        Assert.True(File.Exists(Path.Combine(_leafDir, "a_b.pfx")));
    }

    [Fact]
    public void GetCertificateForHost_LeafDoesNotOutliveRoot()
    {
        // A near-expiry root: a fresh 365-day leaf would otherwise outlive it, which
        // CertificateRequest.Create rejects. The leaf validity must be clamped.
        var rootNotAfter = DateTimeOffset.UtcNow.AddDays(10);
        File.WriteAllBytes(_rootPath, CreateRootPfx(rootNotAfter));

        using var ca = new CertificateAuthority(_rootPath, _leafDir);
        var leaf = ca.GetCertificateForHost("example.com");

        Assert.True(leaf.HasPrivateKey);
        Assert.True(leaf.NotAfter <= ca.RootCertificate.NotAfter);
    }

    private static bool IsCertificateAuthority(X509Certificate2 cert) =>
        cert.Extensions.OfType<X509BasicConstraintsExtension>().FirstOrDefault()?.CertificateAuthority == true;

    private static string GetSanText(X509Certificate2 cert)
    {
        var san = cert.Extensions.FirstOrDefault(e => e.Oid?.Value == "2.5.29.17");
        return san?.Format(false) ?? string.Empty;
    }

    private static byte[] CreateRootPfx(DateTimeOffset NotAfter)
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest("CN=Old Root", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        using var cert = req.CreateSelfSigned(NotAfter.AddDays(-10), NotAfter);
        return cert.Export(X509ContentType.Pkcs12, string.Empty);
    }

    private static byte[] CreateNonCaPfx()
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest("CN=Not A CA", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        using var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        return cert.Export(X509ContentType.Pkcs12, string.Empty);
    }
}
