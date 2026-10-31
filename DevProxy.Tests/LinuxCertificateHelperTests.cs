// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using DevProxy.Proxy;
using Xunit;

namespace DevProxy.Tests;

public class LinuxCertificateHelperTests
{
    private static Func<string, string?> Executables(params string[] names) =>
        name => names.Contains(name) ? $"/usr/sbin/{name}" : null;

    private static Func<string, bool> Directories(params string[] dirs) => dirs.Contains;

    [Fact]
    public void DetectTrustStore_Debian()
    {
        var store = LinuxCertificateHelper.DetectTrustStore(
            Directories("/usr/local/share/ca-certificates"),
            Executables("update-ca-certificates"));

        Assert.NotNull(store);
        Assert.Equal("/usr/local/share/ca-certificates/devproxy-ca.crt", store.CertificatePath);
        Assert.Equal("/usr/local/share/ca-certificates/dev-proxy-ca.crt", store.LegacyCertificatePath);
        Assert.Equal("/etc/ssl/certs/ca-certificates.crt", store.BundlePath);
        Assert.Equal("/usr/sbin/update-ca-certificates", store.UpdateCommand);
        Assert.Empty(store.UpdateArguments);
        Assert.Equal(["--fresh"], store.RemoveUpdateArguments);
    }

    [Fact]
    public void DetectTrustStore_Fedora()
    {
        var store = LinuxCertificateHelper.DetectTrustStore(
            Directories("/etc/pki/ca-trust/source/anchors", "/etc/ca-certificates/trust-source/anchors"),
            Executables("update-ca-trust"));

        Assert.NotNull(store);
        Assert.Equal("/etc/pki/ca-trust/source/anchors/devproxy-ca.crt", store.CertificatePath);
        Assert.Equal("/etc/pki/ca-trust/extracted/pem/tls-ca-bundle.pem", store.BundlePath);
        Assert.Equal("/usr/sbin/update-ca-trust", store.UpdateCommand);
        Assert.Equal(["extract"], store.UpdateArguments);
    }

    [Fact]
    public void DetectTrustStore_Arch()
    {
        var store = LinuxCertificateHelper.DetectTrustStore(
            Directories("/etc/ca-certificates/trust-source/anchors"),
            Executables("update-ca-trust"));

        Assert.NotNull(store);
        Assert.Equal("/etc/ca-certificates/trust-source/anchors/devproxy-ca.crt", store.CertificatePath);
        Assert.Equal("/etc/ca-certificates/extracted/tls-ca-bundle.pem", store.BundlePath);
    }

    [Fact]
    public void DetectTrustStore_PrefersDebianWhenBothToolsExist()
    {
        var store = LinuxCertificateHelper.DetectTrustStore(
            Directories("/usr/local/share/ca-certificates", "/etc/pki/ca-trust/source/anchors"),
            Executables("update-ca-certificates", "update-ca-trust"));

        Assert.NotNull(store);
        Assert.Equal("/usr/local/share/ca-certificates", store.AnchorDirectory);
    }

    [Fact]
    public void DetectTrustStore_UnknownDistro_ReturnsNull()
    {
        Assert.Null(LinuxCertificateHelper.DetectTrustStore(Directories(), Executables()));
        Assert.Null(LinuxCertificateHelper.DetectTrustStore(
            Directories("/usr/local/share/ca-certificates"), Executables()));
        Assert.Null(LinuxCertificateHelper.DetectTrustStore(
            Directories(), Executables("update-ca-trust", "update-ca-certificates")));
    }

    [Fact]
    public void Elevate_Root_RunsAbsoluteCommandDirectly()
    {
        var command = new LinuxCommand("/usr/sbin/update-ca-certificates", []);

        var elevated = LinuxCertificateHelper.Elevate(command, isRoot: true, isInteractive: false, _ => null);

        Assert.Same(command, elevated);
    }

    [Fact]
    public void Elevate_Root_ResolvesBareCommandFromSystemDirectories()
    {
        var elevated = LinuxCertificateHelper.Elevate(
            new("rm", ["-f", "x"]), isRoot: true, isInteractive: false, name => $"/usr/bin/{name}");

        Assert.NotNull(elevated);
        Assert.Equal("/usr/bin/rm", elevated.FileName);
        Assert.Equal(["-f", "x"], elevated.Arguments);
    }

    [Fact]
    public void Elevate_Root_UnresolvedBareCommand_ReturnsNull()
    {
        var elevated = LinuxCertificateHelper.Elevate(
            new("install", []), isRoot: true, isInteractive: false, _ => null);

        Assert.Null(elevated);
    }

    [Fact]
    public void Elevate_Interactive_UsesSudo()
    {
        var elevated = LinuxCertificateHelper.Elevate(
            new("install", ["-m", "0644", "a", "b"]), isRoot: false, isInteractive: true)!;

        Assert.Equal("sudo", elevated.FileName);
        Assert.Equal(["--", "install", "-m", "0644", "a", "b"], elevated.Arguments);
    }

    [Fact]
    public void Elevate_NonInteractive_UsesSudoWithoutPrompt()
    {
        var elevated = LinuxCertificateHelper.Elevate(
            new("rm", ["-f", "x"]), isRoot: false, isInteractive: false)!;

        Assert.Equal("sudo", elevated.FileName);
        Assert.Equal(["-n", "--", "rm", "-f", "x"], elevated.Arguments);
    }

    private static readonly LinuxTrustStore _fedora = new("Fedora/RHEL", "/etc/pki/ca-trust/source/anchors", "/etc/pki/ca-trust/extracted/pem/tls-ca-bundle.pem", "/usr/bin/update-ca-trust", ["extract"], ["extract"]);
    private static readonly LinuxTrustStore _debian = new("Debian/Ubuntu", "/usr/local/share/ca-certificates", "/etc/ssl/certs/ca-certificates.crt", "/usr/sbin/update-ca-certificates", [], ["--fresh"]);

    [Fact]
    public void GetNssDatabaseDirectory_PrefersExistingLegacyDatabase()
    {
        Assert.Equal("/home/u/.pki/nssdb",
            LinuxCertificateHelper.GetNssDatabaseDirectory("/home/u", Directories("/home/u/.pki/nssdb", "/home/u/.local/share/pki/nssdb")));
    }

    [Fact]
    public void GetNssDatabaseDirectory_DefaultsToXdgLocation()
    {
        Assert.Equal("/home/u/.local/share/pki/nssdb",
            LinuxCertificateHelper.GetNssDatabaseDirectory("/home/u", Directories()));
    }

    private static readonly LinuxCommand _install = new("install", ["a", "b"]);
    private static readonly LinuxCommand _refresh = new("update-ca-certificates", []);
    private static readonly LinuxCommand _rollback = new("rm", ["-f", "b"]);

    private static Func<LinuxCommand, CommandOutcome> Outcomes(List<LinuxCommand> ran, params (LinuxCommand Command, CommandOutcome Outcome)[] outcomes) =>
        c =>
        {
            ran.Add(c);
            foreach (var (command, outcome) in outcomes)
            {
                if (command == c)
                {
                    return outcome;
                }
            }
            return CommandOutcome.Succeeded;
        };

    [Fact]
    public void TrustSystemStore_AllSucceed_Trusted()
    {
        List<LinuxCommand> ran = [];

        var outcome = LinuxCertificateHelper.TrustSystemStore([_install, _refresh], _rollback, Outcomes(ran));

        Assert.Equal(SystemTrustOutcome.Trusted, outcome);
        Assert.Equal([_install, _refresh], ran);
    }

    [Fact]
    public void TrustSystemStore_FirstNeedsPassword_SkipsWithoutChanges()
    {
        List<LinuxCommand> ran = [];

        var outcome = LinuxCertificateHelper.TrustSystemStore(
            [_install, _refresh], _rollback, Outcomes(ran, (_install, CommandOutcome.PasswordRequired)));

        Assert.Equal(SystemTrustOutcome.Skipped, outcome);
        Assert.Equal([_install], ran);
    }

    [Fact]
    public void TrustSystemStore_RefreshNeedsPassword_RemovesAnchorAndSkips()
    {
        List<LinuxCommand> ran = [];

        var outcome = LinuxCertificateHelper.TrustSystemStore(
            [_install, _refresh], _rollback, Outcomes(ran, (_refresh, CommandOutcome.PasswordRequired)));

        Assert.Equal(SystemTrustOutcome.Skipped, outcome);
        Assert.Equal([_install, _refresh, _rollback], ran);
    }

    [Fact]
    public void TrustSystemStore_RollbackNeedsPassword_ReportsAnchorLeft()
    {
        List<LinuxCommand> ran = [];

        var outcome = LinuxCertificateHelper.TrustSystemStore(
            [_install, _refresh], _rollback,
            Outcomes(ran, (_refresh, CommandOutcome.PasswordRequired), (_rollback, CommandOutcome.PasswordRequired)));

        Assert.Equal(SystemTrustOutcome.SkippedAnchorLeft, outcome);
    }

    [Fact]
    public void TrustSystemStore_NothingToUndo_SkipsWithoutRollback()
    {
        List<LinuxCommand> ran = [];

        var outcome = LinuxCertificateHelper.TrustSystemStore(
            [_install, _refresh], rollback: null, Outcomes(ran, (_refresh, CommandOutcome.PasswordRequired)));

        Assert.Equal(SystemTrustOutcome.Skipped, outcome);
        Assert.Equal([_install, _refresh], ran);
    }

    [Fact]
    public void TrustSystemStore_OtherFailure_Fails()
    {
        List<LinuxCommand> ran = [];

        var outcome = LinuxCertificateHelper.TrustSystemStore(
            [_install, _refresh], _rollback, Outcomes(ran, (_refresh, CommandOutcome.Failed)));

        Assert.Equal(SystemTrustOutcome.Failed, outcome);
        Assert.Equal([_install, _refresh], ran);
    }

    [Fact]
    public void RunAll_StopsAtFirstFailureAndReportsIt()
    {
        List<LinuxCommand> ran = [];

        var outcome = LinuxCertificateHelper.RunAll(
            [_rollback, _refresh, _install], Outcomes(ran, (_refresh, CommandOutcome.PasswordRequired)));

        Assert.Equal(CommandOutcome.PasswordRequired, outcome);
        Assert.Equal([_rollback, _refresh], ran);
    }

    [Theory]
    [InlineData("sudo: a password is required", true)]
    [InlineData("rm: cannot remove 'x': Read-only file system", false)]
    [InlineData("", false)]
    public void IsPasswordRequired_DetectsSudoMessage(string stderr, bool expected)
    {
        Assert.Equal(expected, LinuxCertificateHelper.IsPasswordRequired(stderr));
    }

    [Fact]
    public void FormatToolError_JoinsLinesAndDropsTrailingPeriods()
    {
        var error = "Error opening input terminal for read\ncertutil: could not change trust: SEC_ERROR_TOKEN_NOT_LOGGED_IN: The token is not logged in..\n";

        Assert.Equal(
            "Error opening input terminal for read certutil: could not change trust: SEC_ERROR_TOKEN_NOT_LOGGED_IN: The token is not logged in",
            LinuxCertificateHelper.FormatToolError(error));
    }

    [Fact]
    public void WriteNewFile_ReplacesSymlinkWithoutTouchingTarget()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var target = Path.Combine(dir.FullName, "target");
            var path = Path.Combine(dir.FullName, "devproxy-ca.crt");
            File.WriteAllText(target, "original");
            _ = File.CreateSymbolicLink(path, target);

            LinuxCertificateHelper.WriteNewFile(path, "cert");

            Assert.Equal("original", File.ReadAllText(target));
            Assert.Null(new FileInfo(path).LinkTarget);
            Assert.Equal("cert", File.ReadAllText(path));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void WriteNewFile_OverwritesExistingFile()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "old");

            LinuxCertificateHelper.WriteNewFile(path, "new");

            Assert.Equal("new", File.ReadAllText(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void GetNssDatabaseDirectoriesForRemoval_ReturnsEveryExistingDatabase()
    {
        Assert.Equal(["/home/u/.pki/nssdb", "/home/u/.local/share/pki/nssdb"],
            LinuxCertificateHelper.GetNssDatabaseDirectoriesForRemoval("/home/u", Directories("/home/u/.pki/nssdb", "/home/u/.local/share/pki/nssdb")));
        Assert.Equal(["/home/u/.local/share/pki/nssdb"],
            LinuxCertificateHelper.GetNssDatabaseDirectoriesForRemoval("/home/u", Directories("/home/u/.local/share/pki/nssdb")));
        Assert.Empty(LinuxCertificateHelper.GetNssDatabaseDirectoriesForRemoval("/home/u", Directories()));
    }

    [Fact]
    public void FindSystemExecutable_IgnoresPath()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var name = $"devproxy-test-{Guid.NewGuid():N}";
            File.WriteAllText(Path.Combine(dir.FullName, name), string.Empty);

            Assert.NotNull(LinuxCertificateHelper.FindExecutable(name, dir.FullName));
            Assert.Null(LinuxCertificateHelper.FindSystemExecutable(name));
        }
        finally
        {
            dir.Delete(true);
        }
    }

    [Fact]
    public void GetTrustCommands_InstallsThenRefreshes()
    {
        var store = _fedora;

        var commands = LinuxCertificateHelper.GetTrustCommands(store, "/tmp/cert.crt");

        Assert.Equal(2, commands.Count);
        Assert.Equal("install", commands[0].FileName);
        Assert.Equal(["-D", "-m", "0644", "/tmp/cert.crt", "/etc/pki/ca-trust/source/anchors/devproxy-ca.crt"], commands[0].Arguments);
        Assert.Equal("/usr/bin/update-ca-trust", commands[1].FileName);
        Assert.Equal(["extract"], commands[1].Arguments);
    }

    [Fact]
    public void GetLegacyCleanupCommands_RemovesLegacyAnchorThenRefreshes()
    {
        var commands = LinuxCertificateHelper.GetLegacyCleanupCommands(_debian);

        Assert.Equal(["rm", "/usr/sbin/update-ca-certificates"], commands.Select(c => c.FileName));
        Assert.Equal(["-f", "/usr/local/share/ca-certificates/dev-proxy-ca.crt"], commands[0].Arguments);
    }

    [Fact]
    public void GetBackupCommand_CopiesAnchorPrivately()
    {
        var command = LinuxCertificateHelper.GetBackupCommand(_debian, "/tmp/devproxy-x/previous.crt");

        Assert.Equal("install", command.FileName);
        Assert.Equal(["-m", "0600", "/usr/local/share/ca-certificates/devproxy-ca.crt", "/tmp/devproxy-x/previous.crt"], command.Arguments);
    }

    [Theory]
    [InlineData(true, null)]
    [InlineData(false, "/dev/null")]
    public void GetNssPasswordFile_EmptyPasswordOnlyWithoutTerminal(bool isInteractive, string? expected)
    {
        Assert.Equal(expected, LinuxCertificateHelper.GetNssPasswordFile(isInteractive));
    }

    // Without a terminal, certutil gets an empty password file so a password-protected database
    // fails (SEC_ERROR_TOKEN_NOT_LOGGED_IN) instead of prompting on /dev/tty.
    [Fact]
    public void GetCertutilCommand_AddsPasswordFileWhenGiven()
    {
        var command = LinuxCertificateHelper.GetCertutilCommand("/usr/bin/certutil", "/tmp/pw", "-L", "-d", "sql:/db", "-n", "Dev Proxy CA");

        Assert.Equal(["-L", "-d", "sql:/db", "-n", "Dev Proxy CA", "-f", "/tmp/pw"], command.Arguments);
    }

    [Fact]
    public void GetCertutilCommand_NoPasswordFileWhenInteractive()
    {
        var command = LinuxCertificateHelper.GetCertutilCommand("/usr/bin/certutil", null, "-D", "-d", "sql:/db", "-n", "Dev Proxy CA");

        Assert.Equal(["-D", "-d", "sql:/db", "-n", "Dev Proxy CA"], command.Arguments);
    }

    [Theory]
    [InlineData(true, "", "Present")]
    [InlineData(false, "certutil: Could not find cert: Dev Proxy CA\n: PR_FILE_NOT_FOUND_ERROR: File not found", "Absent")]
    [InlineData(false, "certutil: function failed: SEC_ERROR_BAD_DATABASE: security library: bad database.", "Unknown")]
    [InlineData(false, "certutil: SEC_ERROR_TOKEN_NOT_LOGGED_IN", "Unknown")]
    public void ClassifyNssLookup_OnlyNotFoundMeansAbsent(bool succeeded, string error, string expected)
    {
        Assert.Equal(Enum.Parse<NssLookup>(expected), LinuxCertificateHelper.ClassifyNssLookup(succeeded, error));
    }

    [Fact]
    public void GetTrustRollback_NothingWhenAnchorAlreadyHoldsCertificate()
    {
        Assert.Null(LinuxCertificateHelper.GetTrustRollback(_debian, anchorHoldsCertificate: true, backupPath: null));
    }

    [Fact]
    public void GetTrustRollback_RestoresPreviousAnchor()
    {
        var rollback = LinuxCertificateHelper.GetTrustRollback(_debian, anchorHoldsCertificate: false, backupPath: "/tmp/backup.crt");

        Assert.NotNull(rollback);
        Assert.Equal("install", rollback.FileName);
        Assert.Equal(["-D", "-m", "0644", "/tmp/backup.crt", "/usr/local/share/ca-certificates/devproxy-ca.crt"], rollback.Arguments);
    }

    [Fact]
    public void GetTrustRollback_RemovesNewAnchor()
    {
        var rollback = LinuxCertificateHelper.GetTrustRollback(_debian, anchorHoldsCertificate: false, backupPath: null);

        Assert.NotNull(rollback);
        Assert.Equal("rm", rollback.FileName);
        Assert.Equal(["-f", "/usr/local/share/ca-certificates/devproxy-ca.crt"], rollback.Arguments);
    }

    [Fact]
    public void GetUntrustCommands_RemovesLegacyAnchorBeforeRefresh()
    {
        var commands = LinuxCertificateHelper.GetUntrustCommands(_debian, removeLegacy: true);

        Assert.Equal(["rm", "rm", "/usr/sbin/update-ca-certificates"], commands.Select(c => c.FileName));
        Assert.Equal(["-f", "/usr/local/share/ca-certificates/dev-proxy-ca.crt"], commands[1].Arguments);
    }

    [Theory]
    [InlineData(false, false, false, false)]
    [InlineData(true, false, false, true)]
    [InlineData(false, true, false, true)]
    [InlineData(false, false, true, true)]
    public void NeedsSystemStoreRemoval(bool anchorExists, bool legacyMatches, bool bundleContains, bool expected) =>
        Assert.Equal(expected, LinuxCertificateHelper.NeedsSystemStoreRemoval(anchorExists, legacyMatches, bundleContains));

    [Fact]
    public void FileContainsCertificate_MatchesOnlyTheSameCertificate()
    {
        using var root = CreateCertificate("CN=Dev Proxy CA");
        using var other = CreateCertificate("CN=Other CA");
        var bundle = Path.GetTempFileName();
        try
        {
            File.WriteAllText(bundle, other.ExportCertificatePem() + "\n" + root.ExportCertificatePem() + "\n");
            Assert.True(LinuxCertificateHelper.FileContainsCertificate(bundle, root));

            File.WriteAllText(bundle, other.ExportCertificatePem());
            Assert.False(LinuxCertificateHelper.FileContainsCertificate(bundle, root));

            File.WriteAllText(bundle, "not a certificate");
            Assert.False(LinuxCertificateHelper.FileContainsCertificate(bundle, root));
        }
        finally
        {
            File.Delete(bundle);
        }

        Assert.False(LinuxCertificateHelper.FileContainsCertificate(bundle, root));
    }

    private static X509Certificate2 CreateCertificate(string subject)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest(subject, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
    }

    [Fact]
    public void GetUntrustCommands_RemovesThenRefreshes()
    {
        var store = _debian;

        var commands = LinuxCertificateHelper.GetUntrustCommands(store, removeLegacy: false);

        Assert.Equal(2, commands.Count);
        Assert.Equal("rm", commands[0].FileName);
        Assert.Equal(["-f", "/usr/local/share/ca-certificates/devproxy-ca.crt"], commands[0].Arguments);
        Assert.Equal("/usr/sbin/update-ca-certificates", commands[1].FileName);
        Assert.Equal(["--fresh"], commands[1].Arguments);
    }
}
