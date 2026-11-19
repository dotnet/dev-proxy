// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Diagnostics;
using System.Security.Cryptography.X509Certificates;

namespace DevProxy.Proxy;

/// <summary>A distro-specific system CA trust store: where anchors go and how to refresh it.</summary>
internal sealed record LinuxTrustStore(
    string Name,
    string AnchorDirectory,
    string BundlePath,
    string UpdateCommand,
    IReadOnlyList<string> UpdateArguments,
    IReadOnlyList<string> RemoveUpdateArguments)
{
    public string CertificatePath => $"{AnchorDirectory}/{LinuxCertificateHelper.CertificateFileName}";

    /// <summary>Anchor that earlier manual setup instructions told users to create.</summary>
    public string LegacyCertificatePath => $"{AnchorDirectory}/{LinuxCertificateHelper.LegacyCertificateFileName}";
}

/// <summary>A process invocation (file name + argument list, no shell).</summary>
internal enum LinuxTrustResult
{
    Trusted,
    /// <summary>The system store trusts the certificate, but NSS-based apps don't.</summary>
    TrustedSystemOnly,
    Skipped,
    Failed,
}

internal enum NssLookup
{
    Present,
    Absent,
    Unknown,
}

internal enum CommandOutcome
{
    Succeeded,
    /// <summary>sudo needed a password and couldn't prompt for it.</summary>
    PasswordRequired,
    Failed,
}

internal enum SystemTrustOutcome
{
    Trusted,
    Skipped,
    /// <summary>Skipped, but the anchor file was added and couldn't be removed again.</summary>
    SkippedAnchorLeft,
    Failed,
}

internal sealed record LinuxCommand(string FileName, IReadOnlyList<string> Arguments)
{
    public override string ToString() => string.Join(' ', [FileName, .. Arguments]);
}

internal static class LinuxCertificateHelper
{
    internal const string CertificateFileName = "devproxy-ca.crt";
    internal const string LegacyCertificateFileName = "dev-proxy-ca.crt";
    internal const string NssNickname = "Dev Proxy CA";

    private static readonly string[] _sbinDirectories = ["/usr/local/sbin", "/usr/sbin", "/sbin"];

    // Root-owned system directories. Tools that run as root are resolved only from here,
    // never from the caller's PATH, so a user-writable PATH entry can't be executed via sudo.
    private static readonly string[] _systemDirectories = ["/usr/sbin", "/usr/bin", "/sbin", "/bin"];

    /// <summary>
    /// Detects the system CA trust store. Supports Debian/Ubuntu (update-ca-certificates),
    /// Fedora/RHEL (update-ca-trust) and Arch (update-ca-trust with trust-source anchors).
    /// Returns null when the distro isn't recognized. <paramref name="findExecutable"/> must
    /// only resolve from trusted system directories because the result runs as root.
    /// </summary>
    internal static LinuxTrustStore? DetectTrustStore(
        Func<string, bool> directoryExists,
        Func<string, string?> findExecutable)
    {
        var updateCaCertificates = findExecutable("update-ca-certificates");
        if (updateCaCertificates is not null && directoryExists("/usr/local/share/ca-certificates"))
        {
            return new(
                "Debian/Ubuntu",
                "/usr/local/share/ca-certificates",
                "/etc/ssl/certs/ca-certificates.crt",
                updateCaCertificates,
                [],
                ["--fresh"]);
        }

        var updateCaTrust = findExecutable("update-ca-trust");
        if (updateCaTrust is not null)
        {
            if (directoryExists("/etc/pki/ca-trust/source/anchors"))
            {
                return new("Fedora/RHEL", "/etc/pki/ca-trust/source/anchors", "/etc/pki/ca-trust/extracted/pem/tls-ca-bundle.pem", updateCaTrust, ["extract"], ["extract"]);
            }

            if (directoryExists("/etc/ca-certificates/trust-source/anchors"))
            {
                return new("Arch", "/etc/ca-certificates/trust-source/anchors", "/etc/ca-certificates/extracted/tls-ca-bundle.pem", updateCaTrust, ["extract"], ["extract"]);
            }
        }

        return null;
    }

    /// <summary>
    /// Wraps a command that needs root. When already root, runs it directly, resolving bare
    /// names from system directories only (PATH could point to user-writable directories);
    /// returns null if it can't be resolved. Otherwise runs it via sudo, with <c>-n</c> when
    /// there's no terminal to prompt on so it fails instead of hanging.
    /// </summary>
    internal static LinuxCommand? Elevate(LinuxCommand command, bool isRoot, bool isInteractive) =>
        Elevate(command, isRoot, isInteractive, FindSystemExecutable);

    internal static LinuxCommand? Elevate(LinuxCommand command, bool isRoot, bool isInteractive, Func<string, string?> findSystemExecutable)
    {
        if (isRoot)
        {
            if (command.FileName.StartsWith('/'))
            {
                return command;
            }
            var resolved = findSystemExecutable(command.FileName);
            return resolved is null ? null : command with { FileName = resolved };
        }

        List<string> args = isInteractive ? [] : ["-n"];
        args.Add("--");
        args.Add(command.FileName);
        args.AddRange(command.Arguments);
        return new("sudo", args);
    }

    // install/rm are passed by name so sudo resolves them through its secure_path.
    internal static IReadOnlyList<LinuxCommand> GetTrustCommands(LinuxTrustStore store, string pemFilePath) =>
    [
        new("install", ["-D", "-m", "0644", pemFilePath, store.CertificatePath]),
        new(store.UpdateCommand, store.UpdateArguments),
    ];

    /// <summary>
    /// Runs only after the new anchor is trusted, so a failure here never leaves the certificate
    /// untrusted. The refresh drops the legacy anchor's links from the store.
    /// </summary>
    internal static IReadOnlyList<LinuxCommand> GetLegacyCleanupCommands(LinuxTrustStore store) =>
    [
        new("rm", ["-f", store.LegacyCertificatePath]),
        new(store.UpdateCommand, store.UpdateArguments),
    ];

    /// <summary>
    /// How to undo the anchor install if trust is skipped: nothing when the anchor already held
    /// this certificate, restore the previous anchor from <paramref name="backupPath"/> when there
    /// was one, otherwise remove the new anchor.
    /// </summary>
    internal static LinuxCommand GetBackupCommand(LinuxTrustStore store, string backupPath) =>
        new("install", ["-m", "0600", store.CertificatePath, backupPath]);

    internal static LinuxCommand? GetTrustRollback(LinuxTrustStore store, bool anchorHoldsCertificate, string? backupPath) =>
        anchorHoldsCertificate ? null
        : backupPath is not null ? new("install", ["-D", "-m", "0644", backupPath, store.CertificatePath])
        : new("rm", ["-f", store.CertificatePath]);

    internal static IReadOnlyList<LinuxCommand> GetUntrustCommands(LinuxTrustStore store, bool removeLegacy)
    {
        List<LinuxCommand> commands = [new("rm", ["-f", store.CertificatePath])];
        if (removeLegacy)
        {
            commands.Add(new("rm", ["-f", store.LegacyCertificatePath]));
        }
        commands.Add(new(store.UpdateCommand, store.RemoveUpdateArguments));
        return commands;
    }

    /// <summary>
    /// Whether <c>cert remove</c> has anything to undo. The bundle check covers a previous
    /// removal where the anchor was deleted but refreshing the store failed.
    /// </summary>
    internal static bool NeedsSystemStoreRemoval(bool anchorExists, bool legacyAnchorMatches, bool bundleContainsCertificate) =>
        anchorExists || legacyAnchorMatches || bundleContainsCertificate;

    /// <summary>Whether the PEM file at <paramref name="path"/> holds <paramref name="certificate"/>.</summary>
    internal static bool FileContainsCertificate(string path, X509Certificate2 certificate)
    {
        try
        {
            if (!File.Exists(path))
            {
                return false;
            }

            var certificates = new X509Certificate2Collection();
            certificates.ImportFromPemFile(path);
            try
            {
                return certificates.Any(c => c.RawData.AsSpan().SequenceEqual(certificate.RawData));
            }
            finally
            {
                foreach (var c in certificates)
                {
                    c.Dispose();
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
        {
            return false;
        }
    }

    internal static string GetNssDatabaseDirectory() =>
        GetNssDatabaseDirectory(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), Directory.Exists);

    /// <summary>
    /// Chromium uses the legacy ~/.pki/nssdb when it exists and ~/.local/share/pki/nssdb
    /// otherwise (M146+). Install and remove share this selection.
    /// </summary>
    internal static string GetNssDatabaseDirectory(string home, Func<string, bool> dirExists)
    {
        var legacy = $"{home}/.pki/nssdb";
        return dirExists(legacy) ? legacy : $"{home}/.local/share/pki/nssdb";
    }

    /// <summary>
    /// Runs the trust commands in order. If sudo needs a password it can't prompt for, nothing
    /// is trusted: when the anchor was already installed, it's removed again with
    /// <paramref name="rollback"/> (null when there's nothing to undo).
    /// </summary>
    internal static SystemTrustOutcome TrustSystemStore(
        IReadOnlyList<LinuxCommand> commands, LinuxCommand? rollback, Func<LinuxCommand, CommandOutcome> run)
    {
        for (var i = 0; i < commands.Count; i++)
        {
            switch (run(commands[i]))
            {
                case CommandOutcome.Succeeded:
                    continue;
                case CommandOutcome.PasswordRequired:
                    if (i == 0 || rollback is null)
                    {
                        return SystemTrustOutcome.Skipped;
                    }
                    return run(rollback) == CommandOutcome.Succeeded
                        ? SystemTrustOutcome.Skipped
                        : SystemTrustOutcome.SkippedAnchorLeft;
                case CommandOutcome.Failed:
                default:
                    return SystemTrustOutcome.Failed;
            }
        }
        return SystemTrustOutcome.Trusted;
    }

    internal static CommandOutcome RunAll(IEnumerable<LinuxCommand> commands, Func<LinuxCommand, CommandOutcome> run)
    {
        foreach (var command in commands)
        {
            var outcome = run(command);
            if (outcome != CommandOutcome.Succeeded)
            {
                return outcome;
            }
        }
        return CommandOutcome.Succeeded;
    }

    internal static bool IsPasswordRequired(string stderr) =>
        stderr.Contains("a password is required", StringComparison.OrdinalIgnoreCase);

    internal static LinuxTrustStore? DetectTrustStore() => DetectTrustStore(Directory.Exists, FindSystemExecutable);

    internal static LinuxTrustResult TrustCertificate(X509Certificate2 certificate, LinuxTrustStore store, ILogger logger)
    {
        var pemFilePath = ExportCertificateToPem(certificate, logger);
        if (pemFilePath is null)
        {
            return LinuxTrustResult.Failed;
        }

        const string skippedMessage = "Skipped trusting the Dev Proxy root certificate because sudo needs a password and there's no terminal to ask for it. To trust the certificate, run 'devproxy cert ensure' in an interactive terminal.";
        DirectoryInfo? backupDir = null;
        string? backupPath = null;
        try
        {
            var (isRoot, isInteractive) = GetContext();
            var removeLegacy = FileContainsCertificate(store.LegacyCertificatePath, certificate);
            var anchorHoldsCertificate = FileContainsCertificate(store.CertificatePath, certificate);

            logger.LogInformation(
                "Adding the Dev Proxy root certificate to the {Distro} system certificate store ({Path}).{SudoHint}",
                store.Name,
                store.CertificatePath,
                isRoot ? string.Empty : " You might be asked for your password.");

            if (!anchorHoldsCertificate && File.Exists(store.CertificatePath))
            {
                // Back up the anchor being replaced so a skipped trust can restore it. Elevated,
                // so it works even if the file isn't readable by the current user.
                backupDir = Directory.CreateTempSubdirectory("devproxy-");
                backupPath = $"{backupDir.FullName}/previous.crt";
                switch (RunElevated(GetBackupCommand(store, backupPath), isRoot, isInteractive, logger))
                {
                    case CommandOutcome.PasswordRequired:
                        logger.LogWarning(skippedMessage);
                        return LinuxTrustResult.Skipped;
                    case CommandOutcome.Failed:
                        return LinuxTrustResult.Failed;
                    case CommandOutcome.Succeeded:
                    default:
                        break;
                }
            }
            var rollback = GetTrustRollback(store, anchorHoldsCertificate, backupPath);

            switch (TrustSystemStore(GetTrustCommands(store, pemFilePath), rollback, c => RunElevated(c, isRoot, isInteractive, logger)))
            {
                case SystemTrustOutcome.Skipped:
                    logger.LogWarning(skippedMessage);
                    return LinuxTrustResult.Skipped;
                case SystemTrustOutcome.SkippedAnchorLeft when backupPath is not null:
                    logger.LogWarning(
                        "Skipped trusting the Dev Proxy root certificate because sudo needs a password and there's no terminal to ask for it. The certificate file {Path} was replaced but the change isn't active, and the previous file couldn't be restored. To finish trusting the certificate, run 'devproxy cert ensure' in an interactive terminal.",
                        store.CertificatePath);
                    return LinuxTrustResult.Skipped;
                case SystemTrustOutcome.SkippedAnchorLeft:
                    logger.LogWarning(
                        "Skipped trusting the Dev Proxy root certificate because sudo needs a password and there's no terminal to ask for it. The certificate file {Path} was added but isn't active, and couldn't be removed. To finish trusting it, run 'devproxy cert ensure' in an interactive terminal. To remove it, run 'sudo rm {Path}'.",
                        store.CertificatePath, store.CertificatePath);
                    return LinuxTrustResult.Skipped;
                case SystemTrustOutcome.Failed:
                    return LinuxTrustResult.Failed;
                case SystemTrustOutcome.Trusted:
                default:
                    break;
            }
            logger.LogInformation("Certificate added to the system certificate store.");

            if (removeLegacy &&
                RunAll(GetLegacyCleanupCommands(store), c => RunElevated(c, isRoot, isInteractive, logger)) != CommandOutcome.Succeeded)
            {
                logger.LogWarning(
                    "Couldn't remove the old certificate file {Path}. It holds the same certificate, so trust isn't affected. To remove it, run 'sudo rm {Path}' and refresh the system certificate store.",
                    store.LegacyCertificatePath, store.LegacyCertificatePath);
            }

            return TrustInNssDatabase(pemFilePath, logger)
                ? LinuxTrustResult.Trusted
                : LinuxTrustResult.TrustedSystemOnly;
        }
        finally
        {
            CleanupFile(pemFilePath);
            try
            {
                // The backup is root-owned, but deleting it only needs write access to our directory.
                backupDir?.Delete(recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // ignore cleanup failures
            }
        }
    }

    /// <summary>Returns false when the system store still trusts the certificate.</summary>
    internal static bool RemoveTrustedCertificate(X509Certificate2 certificate, ILogger logger)
    {
        var removed = true;
        var store = DetectTrustStore();
        var removeLegacy = store is not null && FileContainsCertificate(store.LegacyCertificatePath, certificate);
        if (store is null)
        {
            logger.LogWarning("Remove the Dev Proxy root certificate from your trust store manually.");
        }
        else if (NeedsSystemStoreRemoval(
            File.Exists(store.CertificatePath),
            removeLegacy,
            FileContainsCertificate(store.BundlePath, certificate)))
        {
            var (isRoot, isInteractive) = GetContext();
            logger.LogInformation(
                "Removing the Dev Proxy root certificate from the system certificate store ({Path}).{SudoHint}",
                store.CertificatePath,
                isRoot ? string.Empty : " You might be asked for your password.");

            var outcome = RunAll(GetUntrustCommands(store, removeLegacy), c => RunElevated(c, isRoot, isInteractive, logger));
            if (outcome == CommandOutcome.Succeeded && FileContainsCertificate(store.BundlePath, certificate))
            {
                removed = false;
                logger.LogError(
                    "The system certificate store still trusts the Dev Proxy root certificate: {Bundle} still contains it, so another file in the CA anchors probably holds it. Remove that file and refresh the system certificate store.",
                    store.BundlePath);
            }
            else if (outcome == CommandOutcome.Succeeded)
            {
                logger.LogInformation("Certificate removed from the system certificate store.");
            }
            else
            {
                removed = false;
                logger.LogError(
                    "The system certificate store still trusts the Dev Proxy root certificate{Reason}. To finish removing it, run 'devproxy cert remove' in an interactive terminal.",
                    outcome == CommandOutcome.PasswordRequired ? " because sudo needs a password and there's no terminal to ask for it" : string.Empty);
            }
        }

        // Run NSS removal even if the system store step failed, so it's as complete as possible.
        return RemoveFromNssDatabase(logger) && removed;
    }

    /// <summary>Returns false when certutil is available but couldn't add the certificate.</summary>
    private static bool TrustInNssDatabase(string pemFilePath, ILogger logger)
    {
        var certutil = FindCertutil();
        if (certutil is null)
        {
            logger.LogInformation(
                "To have Chrome and other NSS-based apps trust the certificate, install certutil (libnss3-tools on Debian/Ubuntu, nss-tools on Fedora/RHEL, nss on Arch) and run 'devproxy cert ensure' again.");
            return true;
        }

        var nssDir = GetNssDatabaseDirectory();
        return TrustInNssDatabase(certutil, nssDir, GetNssPasswordFile(), pemFilePath, logger);
    }

    private static bool TrustInNssDatabase(string certutil, string nssDir, string? passwordFile, string pemFilePath, ILogger logger)
    {
        var db = $"sql:{nssDir}";
        string error;
        if (!Directory.Exists(nssDir))
        {
            try
            {
                _ = Directory.CreateDirectory(nssDir);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                LogNssTrustFailure(logger, nssDir, ex.Message, NssLookup.Absent);
                return false;
            }
            error = Execute(new(certutil, ["-N", "-d", db, "--empty-password"]), redirectOutput: true).Error;
            if (error.Length > 0)
            {
                LogNssTrustFailure(logger, nssDir, error, NssLookup.Absent);
                return false;
            }
        }

        // Replace any previous Dev Proxy root (it's regenerated per machine) so re-runs are idempotent.
        _ = Run(GetCertutilCommand(certutil, passwordFile, "-D", "-d", db, "-n", NssNickname), logger, redirectOutput: true, logFailure: false);
        error = Execute(GetCertutilCommand(certutil, passwordFile, "-A", "-d", db, "-n", NssNickname, "-t", "C,,", "-i", pemFilePath), redirectOutput: true).Error;
        if (error.Length == 0)
        {
            logger.LogInformation("Certificate added to the NSS database ({Path}).", nssDir);
            return true;
        }

        // A failed add can leave an entry without trust flags behind; try to remove it.
        var leftover = LookUpNssEntry(certutil, passwordFile, db);
        if (leftover == NssLookup.Present &&
            Run(GetCertutilCommand(certutil, passwordFile, "-D", "-d", db, "-n", NssNickname), logger, redirectOutput: true, logFailure: false))
        {
            leftover = NssLookup.Absent;
        }
        LogNssTrustFailure(logger, nssDir, error, leftover);
        return false;
    }

    /// <summary>
    /// Without a terminal, certutil reads an empty password from /dev/null (<c>-f</c>) so a
    /// password-protected database fails with SEC_ERROR_TOKEN_NOT_LOGGED_IN instead of prompting
    /// on /dev/tty, which it does even when stdin is redirected. Databases without a password work
    /// as usual. Null when interactive, so certutil can prompt.
    /// </summary>
    internal static string? GetNssPasswordFile(bool isInteractive) => isInteractive ? null : "/dev/null";

    private static string? GetNssPasswordFile() => GetNssPasswordFile(GetContext().IsInteractive);

    internal static LinuxCommand GetCertutilCommand(string certutil, string? passwordFile, params string[] arguments) =>
        new(certutil, passwordFile is null ? arguments : [.. arguments, "-f", passwordFile]);

    private static NssLookup LookUpNssEntry(string certutil, string? passwordFile, string db)
    {
        var (succeeded, error) = Execute(GetCertutilCommand(certutil, passwordFile, "-L", "-d", db, "-n", NssNickname), redirectOutput: true);
        return ClassifyNssLookup(succeeded, error);
    }

    /// <summary>Only certutil's "not found" error means there's no entry; other errors mean it couldn't check.</summary>
    internal static NssLookup ClassifyNssLookup(bool succeeded, string error) =>
        succeeded ? NssLookup.Present
        : error.Contains("PR_FILE_NOT_FOUND_ERROR", StringComparison.Ordinal) ? NssLookup.Absent
        : NssLookup.Unknown;

    internal static string FormatToolError(string error) =>
        string.Join(' ', error.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)).TrimEnd('.');

    private static void LogNssTrustFailure(ILogger logger, string nssDir, string error, NssLookup leftover)
    {
        logger.LogWarning(
            "The system certificate store trusts the Dev Proxy root certificate, but Chrome and other NSS-based apps don't: couldn't add it to the NSS database ({Path}): {Error}. If the database has a password, run 'devproxy cert ensure' in an interactive terminal. Otherwise, check that you can write to it.",
            nssDir, FormatToolError(error));
        if (leftover == NssLookup.Present)
        {
            logger.LogWarning(
                "The NSS database still has a '{Nickname}' entry that couldn't be removed. Remove it with: certutil -D -d sql:{Path} -n \"{Nickname}\"",
                NssNickname, nssDir, NssNickname);
        }
        else if (leftover == NssLookup.Unknown)
        {
            logger.LogWarning(
                "Couldn't check whether the NSS database has a '{Nickname}' entry left without trust. If it does, remove it with: certutil -D -d sql:{Path} -n \"{Nickname}\"",
                NssNickname, nssDir, NssNickname);
        }
    }

    /// <summary>Returns false when certutil found the certificate but couldn't delete it.</summary>
    private static bool RemoveFromNssDatabase(ILogger logger)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var nssDirs = GetNssDatabaseDirectoriesForRemoval(home, Directory.Exists).ToArray();
        if (nssDirs.Length == 0)
        {
            return true;
        }

        var certutil = FindCertutil();
        if (certutil is null)
        {
            logger.LogWarning(
                "Couldn't find certutil to check the NSS database ({Paths}). If you added the Dev Proxy root certificate there, remove '{Nickname}' manually or install certutil and run 'devproxy cert remove' again.",
                string.Join(", ", nssDirs), NssNickname);
            return true;
        }

        return RemoveFromNssDatabases(certutil, nssDirs, GetNssPasswordFile(), logger);
    }

    private static bool RemoveFromNssDatabases(string certutil, string[] nssDirs, string? passwordFile, ILogger logger)
    {
        var removed = true;
        foreach (var nssDir in nssDirs)
        {
            var db = $"sql:{nssDir}";
            // Only attempt (and report failures of) removal when the certificate is there.
            var lookup = LookUpNssEntry(certutil, passwordFile, db);
            if (lookup == NssLookup.Absent)
            {
                continue;
            }
            if (lookup == NssLookup.Unknown)
            {
                removed = false;
                logger.LogError(
                    "Couldn't check the NSS database ({Path}) for the Dev Proxy root certificate. If it's there, remove '{Nickname}' manually.",
                    nssDir, NssNickname);
                continue;
            }
            if (Run(GetCertutilCommand(certutil, passwordFile, "-D", "-d", db, "-n", NssNickname), logger, redirectOutput: true))
            {
                logger.LogInformation("Certificate removed from the NSS database ({Path}).", nssDir);
            }
            else
            {
                removed = false;
                logger.LogError("Couldn't remove the Dev Proxy root certificate from the NSS database ({Path}). Remove '{Nickname}' manually.", nssDir, NssNickname);
            }
        }
        return removed;
    }

    /// <summary>
    /// The active NSS database can change (e.g. ~/.pki/nssdb is created after the certificate was
    /// added to ~/.local/share/pki/nssdb), so removal checks every database that exists.
    /// </summary>
    internal static IEnumerable<string> GetNssDatabaseDirectoriesForRemoval(string home, Func<string, bool> dirExists) =>
        new[] { $"{home}/.pki/nssdb", $"{home}/.local/share/pki/nssdb" }.Where(dirExists);

    /// <summary>
    /// Replaces rather than follows an existing entry: deleting a symlink doesn't touch its target,
    /// and CreateNew fails if something is recreated at the path in between.
    /// </summary>
    internal static void WriteNewFile(string path, string contents)
    {
        File.Delete(path);
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
        using var writer = new StreamWriter(stream);
        writer.Write(contents);
    }

    /// <summary>
    /// Writes the public root certificate to devproxy-ca.crt in the current directory so the user
    /// can add it to their CA anchors, regardless of where the PFX is stored.
    /// </summary>
    internal static void WriteManualTrustInstructions(X509Certificate2 certificate, ILogger logger)
    {
        const string help = "More information: https://aka.ms/devproxy/start";
        var path = Path.GetFullPath(CertificateFileName);
        try
        {
            WriteNewFile(path, certificate.ExportCertificatePem() + "\n");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(
                "Couldn't detect your distro's certificate store, so trust the Dev Proxy root certificate manually. Couldn't save it to {Path}: {Error}. {Help}",
                path, ex.Message, help);
            return;
        }

        logger.LogWarning(
            "Couldn't detect your distro's certificate store. Saved the Dev Proxy root certificate to {Path}. Add it to your system's CA anchors and refresh the store. {Help}",
            path, help);
    }

    private static (bool IsRoot, bool IsInteractive) GetContext() =>
        (Environment.UserName == "root",
         !Console.IsInputRedirected && Environment.GetEnvironmentVariable("CI") is null);

    private static CommandOutcome RunElevated(LinuxCommand command, bool isRoot, bool isInteractive, ILogger logger)
    {
        var elevated = Elevate(command, isRoot, isInteractive);
        if (elevated is null)
        {
            logger.LogError("Couldn't find {Command} in {Directories}.", command.FileName, string.Join(", ", _systemDirectories));
            return CommandOutcome.Failed;
        }

        // Without a terminal (sudo -n), capture stderr to tell a missing password from other failures.
        var captureError = !isRoot && !isInteractive;
        var (succeeded, error) = Execute(elevated, redirectOutput: captureError);
        if (succeeded)
        {
            return CommandOutcome.Succeeded;
        }
        if (captureError && IsPasswordRequired(error))
        {
            return CommandOutcome.PasswordRequired;
        }
        logger.LogError("Command '{Command}' failed{Error}", elevated, error.Length > 0 ? $": {error}" : ".");
        return CommandOutcome.Failed;
    }

    // When running as root, don't pick up certutil from PATH, which might be user-writable.
    private static string? FindCertutil() =>
        GetContext().IsRoot ? FindSystemExecutable("certutil") : FindExecutable("certutil");

    internal static string? FindSystemExecutable(string name) =>
        _systemDirectories.Select(d => Path.Combine(d, name)).FirstOrDefault(File.Exists);

    internal static string? FindExecutable(string name) =>
        FindExecutable(name, Environment.GetEnvironmentVariable("PATH"));

    internal static string? FindExecutable(string name, string? path)
    {
        var directories = (path ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Concat(_sbinDirectories);
        foreach (var directory in directories)
        {
            var candidate = Path.Combine(directory, name);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }
        return null;
    }

    private static bool Run(LinuxCommand command, ILogger logger, bool redirectOutput, bool logFailure = true)
    {
        var (succeeded, error) = Execute(command, redirectOutput);
        if (!succeeded && logFailure)
        {
            logger.LogError("Command '{Command}' failed{Error}", command, error.Length > 0 ? $": {error}" : ".");
        }
        return succeeded;
    }

    /// <summary>
    /// Runs the command. On failure, Error has the captured stderr (when redirected), the exit
    /// code, or why it couldn't start; it's empty on success.
    /// </summary>
    private static (bool Succeeded, string Error) Execute(LinuxCommand command, bool redirectOutput)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = command.FileName,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = redirectOutput,
            RedirectStandardError = redirectOutput,
        };
        foreach (var argument in command.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }
        if (redirectOutput)
        {
            // Untranslated messages, so failures like sudo's "a password is required" can be detected.
            startInfo.Environment["LC_ALL"] = "C";
        }

        try
        {
            using var process = new Process { StartInfo = startInfo };
            var stderr = new System.Text.StringBuilder();
            if (redirectOutput)
            {
                process.ErrorDataReceived += (_, e) => stderr.AppendLine(e.Data);
            }
            _ = process.Start();
            if (redirectOutput)
            {
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
            }
            process.WaitForExit();

            if (process.ExitCode == 0)
            {
                return (true, string.Empty);
            }
            var error = stderr.ToString().Trim();
            return (false, error.Length > 0 ? error : $"exit code {process.ExitCode}");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return (false, ex.Message);
        }
    }

    private static string? ExportCertificateToPem(X509Certificate2 certificate, ILogger logger)
    {
        try
        {
            // Public certificate only; the private key never leaves the PFX.
            return WriteTempFile(certificate.ExportCertificatePem() + "\n");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
        {
            logger.LogError(ex, "Failed to export the root certificate to PEM");
            return null;
        }
    }

    private static string WriteTempFile(string contents)
    {
        var path = Path.Combine(Path.GetTempPath(), $"devproxy-ca-{Guid.NewGuid():N}.crt");
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }
        using var stream = new FileStream(path, options);
        using var writer = new StreamWriter(stream);
        writer.Write(contents);
        return path;
    }

    private static void CleanupFile(string filePath)
    {
        try
        {
            File.Delete(filePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // ignore cleanup failures
        }
    }
}
