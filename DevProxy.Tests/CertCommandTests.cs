// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.CommandLine;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using DevProxy.Abstractions.Proxy;
using DevProxy.Commands;
using DevProxy.Proxy;
using DevProxy.Proxy.Kestrel.Internal;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DevProxy.Tests;

public sealed class CertCommandTests : IDisposable
{
    private readonly X509Certificate2 _certificate;
    private readonly string _caDirectory = Path.Combine(Path.GetTempPath(), "devproxy-certcmd-ca-" + Guid.NewGuid().ToString("N"));
    private CertificateAuthority? _certificateAuthority;

    public CertCommandTests()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=Dev Proxy Test CA", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        _certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
    }

    public void Dispose()
    {
        _certificate.Dispose();
        _certificateAuthority?.Dispose();
        if (Directory.Exists(_caDirectory))
        {
            Directory.Delete(_caDirectory, recursive: true);
        }
    }

    private CertificateAuthority GetCertificateAuthority() =>
        _certificateAuthority ??= new CertificateAuthority(
            Path.Combine(_caDirectory, "rootCert.pfx"),
            Path.Combine(_caDirectory, "certificates"));

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
    public void IsSameFile_IndependentCopy_ReturnsFalse()
    {
        var root = Path.Combine(_caDirectory, "rootCert.pfx");
        var copy = Path.Combine(_caDirectory, "copy.pfx");
        _ = Directory.CreateDirectory(_caDirectory);
        File.WriteAllBytes(root, [1, 2, 3]);
        File.Copy(root, copy);

        Assert.False(CertCommand.IsSameFile(copy, root));
    }

    [Fact]
    public void IsSameFile_DetectsHardLink()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var root = Path.Combine(_caDirectory, "rootCert.pfx");
        var link = Path.Combine(_caDirectory, "link.pfx");
        _ = Directory.CreateDirectory(_caDirectory);
        File.WriteAllBytes(root, [1, 2, 3]);
        using (var ln = System.Diagnostics.Process.Start("ln", [root, link]))
        {
            ln.WaitForExit();
            Assert.Equal(0, ln.ExitCode);
        }

        Assert.True(CertCommand.IsSameFile(link, root));
    }

    [Fact]
    public void IsSameFile_PathDifferingInCase_FollowsFileSystemCaseSensitivity()
    {
        var root = Path.Combine(_caDirectory, "rootCert.pfx");
        var differentCase = Path.Combine(_caDirectory, "ROOTCERT.PFX");
        _ = Directory.CreateDirectory(_caDirectory);
        File.WriteAllBytes(root, [1, 2, 3]);

        if (File.Exists(differentCase))
        {
            Assert.True(CertCommand.IsSameFile(differentCase, root));
        }
        else
        {
            File.WriteAllBytes(differentCase, [4, 5, 6]);
            Assert.False(CertCommand.IsSameFile(differentCase, root));
        }
    }

    [Theory]
    [InlineData(null, "ca.pem", "pem")]
    [InlineData("json", "ca.der", "der")]
    public void Export_WritesCertificateAndPrintsResult(string? output, string fileName, string expectedFormat)
    {
        var ca = GetCertificateAuthority();
        var file = Path.Combine(_caDirectory, "out", fileName);
        var args = new List<string> { "cert", "export", "--file", file };
        if (output is not null)
        {
            args.AddRange(["--output", output]);
        }
        var (exitCode, stdout) = InvokeExport(ca, [.. args]);

        Assert.Equal(0, exitCode);
        using var exported = X509CertificateLoader.LoadCertificateFromFile(file);
        Assert.Equal(ca.RootCertificate.Thumbprint, exported.Thumbprint);
        Assert.False(exported.HasPrivateKey);

        if (output is null)
        {
            Assert.Equal(Path.GetFullPath(file), stdout.Trim());
        }
        else
        {
            using var json = System.Text.Json.JsonDocument.Parse(stdout);
            Assert.Equal(Path.GetFullPath(file), json.RootElement.GetProperty("path").GetString());
            Assert.Equal(expectedFormat, json.RootElement.GetProperty("format").GetString());
        }
    }

    [Fact]
    public void Export_OverwritesExistingFile()
    {
        var ca = GetCertificateAuthority();
        var file = Path.Combine(_caDirectory, "ca.pem");
        File.WriteAllText(file, "stale");

        var (exitCode, _) = InvokeExport(ca, ["cert", "export", "--file", file]);

        Assert.Equal(0, exitCode);
        Assert.StartsWith("-----BEGIN CERTIFICATE-----", File.ReadAllText(file), StringComparison.Ordinal);
    }

    [Fact]
    public void Export_ToStoredRoot_FailsAndLeavesRootUnchanged()
    {
        var ca = GetCertificateAuthority();
        var rootBytes = File.ReadAllBytes(ca.RootCertificatePath);

        var (exitCode, stdout) = InvokeExport(ca, ["cert", "export", "--file", ca.RootCertificatePath]);

        Assert.Equal(1, exitCode);
        Assert.Empty(stdout);
        Assert.Equal(rootBytes, File.ReadAllBytes(ca.RootCertificatePath));
    }

    [Fact]
    public void Export_ToCopyOfStoredRoot_Succeeds()
    {
        var ca = GetCertificateAuthority();
        var copy = Path.Combine(_caDirectory, "copy.pfx");
        File.Copy(ca.RootCertificatePath, copy);
        var rootBytes = File.ReadAllBytes(ca.RootCertificatePath);

        var (exitCode, _) = InvokeExport(ca, ["cert", "export", "--format", "der", "--file", copy]);

        Assert.Equal(0, exitCode);
        Assert.Equal(ca.RootCertificate.RawData, File.ReadAllBytes(copy));
        Assert.Equal(rootBytes, File.ReadAllBytes(ca.RootCertificatePath));
    }

    [Fact]
    public void Export_RootNotPersisted_Fails()
    {
        // A file where the root's parent directory should be makes the save fail.
        var blocker = Path.Combine(_caDirectory, "blocker");
        _ = Directory.CreateDirectory(_caDirectory);
        File.WriteAllText(blocker, string.Empty);
        using var ca = new CertificateAuthority(Path.Combine(blocker, "rootCert.pfx"), Path.Combine(_caDirectory, "certificates"));
        var file = Path.Combine(_caDirectory, "ca.pem");

        var (exitCode, stdout) = InvokeExport(ca, ["cert", "export", "--file", file]);

        Assert.Equal(1, exitCode);
        Assert.Empty(stdout);
        Assert.False(File.Exists(file));
    }

    [Theory]
    [InlineData(new[] { "cert", "export" }, true)]
    [InlineData(new[] { "--log-level", "debug", "cert", "--output", "json", "export" }, true)]
    [InlineData(new[] { "cert", "export", "--format", "der", "--file", "ca.der" }, true)]
    [InlineData(new[] { "--watch-process-names", "cert", "export" }, false)]
    [InlineData(new[] { "-u", "cert", "export" }, true)]
    [InlineData(new[] { "--watch-process-names", "node", "cert", "export" }, true)]
    [InlineData(new[] { "jwt", "create", "--name", "cert", "--scopes", "export" }, false)]
    [InlineData(new[] { "--failure-rate", "10", "cert", "export" }, true)]
    [InlineData(new[] { "--failure-rate", "10", "jwt", "create", "--name", "cert", "--scopes", "export", "--output", "json" }, false)]
    [InlineData(new[] { "--mock-flag", "config", "get", "cert", "export" }, false)]
    [InlineData(new[] { "--mock-flag", "jwt", "create", "--name", "cert", "--scopes", "export" }, false)]
    [InlineData(new[] { "--mocks-file", "config", "cert", "export", "--output", "json" }, true)]
    [InlineData(new[] { "--mocks-file", "config", "--failure-rate", "10", "cert", "export" }, true)]
    [InlineData(new[] { "--allowed-errors", "429", "500", "cert", "export" }, true)]
    [InlineData(new[] { "--mock-flag", "cert", "export" }, true)]
    [InlineData(new[] { "--mocks-file", "cert", "jwt", "create", "--scopes", "export", "--output", "json" }, false)]
    [InlineData(new[] { "cert", "ensure" }, false)]
    [InlineData(new[] { "export" }, false)]
    public void IsCertExportInvocation_DetectsCommand(string[] args, bool expected)
    {
        Assert.Equal(expected, DevProxyCommand.IsCertExportInvocation(args));
    }

    [Fact]
    public void IsCertExportInvocation_ExpandsResponseFile()
    {
        _ = Directory.CreateDirectory(_caDirectory);
        var responseFile = Path.Combine(_caDirectory, "export.rsp");
        File.WriteAllText(responseFile, "cert export");

        Assert.True(DevProxyCommand.IsCertExportInvocation(["@" + responseFile]));
    }

    private static (int ExitCode, string Stdout) InvokeExport(CertificateAuthority ca, string[] args)
    {
        var root = new RootCommand
        {
            new Option<OutputFormat?>(DevProxyCommand.OutputOptionName) { Recursive = true },
            new CertCommand(NullLogger<CertCommand>.Instance, ca.RootCertificate, ca, new TestCertificateTrust(fail: false))
        };
        using var stdout = new StringWriter();
        var exitCode = root.Parse(args).Invoke(new InvocationConfiguration { Output = stdout });
        return (exitCode, stdout.ToString());
    }

    [Fact]
    public void GetCertificateBytes_Der_ReturnsRawCertificate()
    {
        var der = CertCommand.GetCertificateBytes(_certificate, CertExportFormat.Der);

        Assert.Equal(_certificate.RawData, der);
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 1)]
    public void Ensure_ReportsTrustResult(bool fail, int expectedExitCode)
    {
        using var certificate = CreateCertificate();
        var trust = new TestCertificateTrust(fail);
        var command = new CertCommand(NullLogger<CertCommand>.Instance, certificate, GetCertificateAuthority(), trust);

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
        var command = new CertCommand(NullLogger<CertCommand>.Instance, certificate, GetCertificateAuthority(), trust);

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
