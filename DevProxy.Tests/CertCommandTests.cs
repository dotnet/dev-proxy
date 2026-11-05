// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Commands;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

using Xunit;

namespace DevProxy.Tests;

public sealed class CertCommandTests : IDisposable
{
    private readonly X509Certificate2 _certificate;

    public CertCommandTests()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=Dev Proxy Test CA", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        _certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
    }

    public void Dispose() => _certificate.Dispose();

    [Theory]
    [InlineData("Pem", null, "Pem")]
    [InlineData("Der", "ca.crt", "Der")]
    [InlineData(null, null, "Pem")]
    [InlineData(null, "ca.pem", "Pem")]
    [InlineData(null, "ca.CRT", "Crt")]
    [InlineData(null, "ca.der", "Der")]
    [InlineData(null, "ca.txt", "Pem")]
    public void ResolveExportFormat_ReturnsExpectedFormat(string? format, string? file, string expected)
    {
        CertExportFormat? requested = format is null ? null : Enum.Parse<CertExportFormat>(format);

        Assert.Equal(Enum.Parse<CertExportFormat>(expected), CertCommand.ResolveExportFormat(requested, file));
    }

    [Theory]
    [InlineData("Pem", "dev-proxy-ca.pem")]
    [InlineData("Crt", "dev-proxy-ca.crt")]
    [InlineData("Der", "dev-proxy-ca.der")]
    public void GetDefaultExportFileName_UsesFormatExtension(string format, string expected)
    {
        Assert.Equal(expected, CertCommand.GetDefaultExportFileName(Enum.Parse<CertExportFormat>(format)));
    }

    [Theory]
    [InlineData("Pem")]
    [InlineData("Crt")]
    public void GetCertificateBytes_TextFormats_ReturnPublicPem(string format)
    {
        var pem = Encoding.ASCII.GetString(CertCommand.GetCertificateBytes(_certificate, Enum.Parse<CertExportFormat>(format)));

        Assert.StartsWith("-----BEGIN CERTIFICATE-----\n", pem, StringComparison.Ordinal);
        Assert.EndsWith("-----END CERTIFICATE-----\n", pem, StringComparison.Ordinal);
        Assert.DoesNotContain("PRIVATE KEY", pem, StringComparison.Ordinal);

        using var roundTripped = X509Certificate2.CreateFromPem(pem);
        Assert.Equal(_certificate.Thumbprint, roundTripped.Thumbprint);
        Assert.False(roundTripped.HasPrivateKey);
    }

    [Fact]
    public void IsSameFile_NormalizesRelativeSegments()
    {
        var root = Path.Combine(Path.GetTempPath(), "devproxy", "rootCert.pfx");
        var relative = Path.Combine(Path.GetTempPath(), "devproxy", "sub", "..", "rootCert.pfx");

        Assert.True(CertCommand.IsSameFile(relative, root));
        Assert.False(CertCommand.IsSameFile(Path.Combine(Path.GetTempPath(), "devproxy", "ca.pem"), root));
    }

    [Fact]
    public void IsSameFile_DetectsSymlinkedPath()
    {
        var dir = Path.Combine(Path.GetTempPath(), "devproxy-certcmd-" + Guid.NewGuid().ToString("N"));
        var target = Path.Combine(dir, "target");
        var link = Path.Combine(dir, "link");
        _ = Directory.CreateDirectory(target);
        try
        {
            _ = Directory.CreateSymbolicLink(link, target);
            File.WriteAllBytes(Path.Combine(target, "rootCert.pfx"), [1, 2, 3]);
            File.WriteAllBytes(Path.Combine(target, "other.pfx"), [4, 5, 6]);

            Assert.True(CertCommand.IsSameFile(Path.Combine(link, "rootCert.pfx"), Path.Combine(target, "rootCert.pfx")));
            Assert.False(CertCommand.IsSameFile(Path.Combine(link, "other.pfx"), Path.Combine(target, "rootCert.pfx")));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Creating symbolic links may require elevated privileges on Windows.
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void GetCertificateBytes_Der_ReturnsRawCertificate()
    {
        var der = CertCommand.GetCertificateBytes(_certificate, CertExportFormat.Der);

        Assert.Equal(_certificate.RawData, der);
    }
}
