// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.CommandLine;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using DevProxy.Abstractions.Proxy;
using DevProxy.Commands;
using DevProxy.Proxy;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DevProxy.Tests;

public class CertCommandTests
{
    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 1)]
    public void Ensure_ReportsTrustResult(bool fail, int expectedExitCode)
    {
        using var certificate = CreateCertificate();
        var trust = new TestCertificateTrust(fail);
        var command = new CertCommand(NullLogger<CertCommand>.Instance, certificate, trust);

        Assert.Equal(expectedExitCode, command.Parse("ensure").Invoke());
        Assert.True(trust.TrustCalled);
    }

    [Fact]
    public void Ensure_ExportFailure_ReturnsNonZeroOnSupportedPlatforms()
    {
        if (!OperatingSystem.IsMacOS() && !OperatingSystem.IsWindows())
        {
            return;
        }

        using var certificate = CreateCertificate();
        certificate.Dispose();
        var trust = new RootCertificateTrust(new FakeProxyConfiguration(), NullLogger<RootCertificateTrust>.Instance);
        var command = new CertCommand(NullLogger<CertCommand>.Instance, certificate, trust);

        Assert.Equal(1, command.Parse("ensure").Invoke());
    }

    [Fact]
    public void MacTrust_ExportFailure_ReturnsFalseWithoutThrowing()
    {
        using var certificate = CreateCertificate();
        certificate.Dispose();

        Assert.False(MacCertificateHelper.TrustCertificate(certificate, NullLogger.Instance));
    }

    [Fact]
    public void MacSecurityCommand_NonZeroExit_ReturnsFalseWithoutThrowing()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        Assert.False(MacCertificateHelper.RunSecurityCommand(
            "devproxy-invalid-command", "test", NullLogger.Instance));
    }

    [Fact]
    public void EnsureTrusted_WindowsInstallFailure_DoesNotThrow()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var certificate = CreateCertificate();
        certificate.Dispose();
        var trust = new RootCertificateTrust(
            new FakeProxyConfiguration { InstallCert = true },
            NullLogger<RootCertificateTrust>.Instance);

        trust.EnsureTrusted(certificate);
    }

    private static X509Certificate2 CreateCertificate()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=DevProxy test", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(1));
    }

    private sealed class TestCertificateTrust(bool fail) : IRootCertificateTrust
    {
        public bool TrustCalled { get; private set; }

        public void EnsureTrusted(X509Certificate2 rootCertificate) =>
            throw new NotSupportedException();

        public void Trust(X509Certificate2 rootCertificate)
        {
            TrustCalled = true;
            if (fail)
            {
                throw new InvalidOperationException("Certificate installation failed.");
            }
        }

        public void Untrust(X509Certificate2 rootCertificate) =>
            throw new NotSupportedException();
    }
}
