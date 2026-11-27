// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Abstractions.Proxy;
using DevProxy.Abstractions.Utils;
using DevProxy.Logging;
using DevProxy.Proxy;
using DevProxy.Proxy.Kestrel.Internal;
using System.CommandLine;
using System.CommandLine.Parsing;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;

namespace DevProxy.Commands;

enum CertExportFormat
{
    Pem,
    Crt,
    Der
}

sealed class CertCommand : Command
{
    private readonly ILogger _logger;
    // Resolved on use, because creating the root certificate logs diagnostics that
    // cert export must route to stderr first
    private readonly Lazy<X509Certificate2> _rootCertificate;
    private readonly Lazy<CertificateAuthority> _certificateAuthority;
    private readonly IRootCertificateTrust _rootCertificateTrust;
    private readonly ConsoleLogRouting? _logRouting;
    private readonly Option<bool> _forceOption = new("--force", "-f")
    {
        Description = "Don't prompt for confirmation when removing the certificate. Required for non-interactive use (CI, piped stdin, automation)."
    };
    private readonly Option<CertExportFormat?> _exportFormatOption = new("--format")
    {
        Description = "Certificate format: pem, crt (PEM-encoded) or der (binary). Defaults to the format matching the --file extension, or pem.",
        HelpName = "pem|crt|der"
    };
    private readonly Option<string?> _exportFileOption = new("--file")
    {
        Description = "Path of the file to write the certificate to. Defaults to dev-proxy-ca.<format> in the current directory. Overwrites existing files.",
        HelpName = "path"
    };

    internal const string DefaultExportFileName = "dev-proxy-ca";

    public CertCommand(
        ILogger<CertCommand> logger,
        IServiceProvider serviceProvider,
        IRootCertificateTrust rootCertificateTrust) :
        this(
            logger,
            serviceProvider.GetRequiredService<X509Certificate2>,
            serviceProvider.GetRequiredService<CertificateAuthority>,
            rootCertificateTrust,
            serviceProvider.GetService<ConsoleLogRouting>())
    {
    }

    internal CertCommand(
        ILogger<CertCommand> logger,
        Func<X509Certificate2> rootCertificate,
        Func<CertificateAuthority> certificateAuthority,
        IRootCertificateTrust rootCertificateTrust,
        ConsoleLogRouting? logRouting = null) :
        base("cert", "Manage the Dev Proxy certificate")
    {
        _logger = logger;
        _rootCertificate = new(rootCertificate);
        _certificateAuthority = new(certificateAuthority);
        _rootCertificateTrust = rootCertificateTrust;
        _logRouting = logRouting;

        ConfigureCommand();
    }

    private void ConfigureCommand()
    {
        var certEnsureCommand = new Command("ensure", "Ensure certificates are setup (creates root if required). Also makes root certificate trusted.");
        certEnsureCommand.SetAction(_ => EnsureCert());

        var certRemoveCommand = new Command("remove", "Remove the certificate from Root Store");
        certRemoveCommand.SetAction(RemoveCert);
        certRemoveCommand.AddOptions(new[] { _forceOption }.OrderByName());

        var certExportCommand = new Command("export", "Write the public root certificate to a file (creates root if required). Doesn't require a running Dev Proxy instance.");
        certExportCommand.SetAction(ExportCert);
        certExportCommand.AddOptions(new Option[] { _exportFormatOption, _exportFileOption }.OrderByName());

        this.AddCommands(new List<Command>
        {
            certEnsureCommand,
            certExportCommand,
            certRemoveCommand,
        }.OrderByName());

        HelpExamples.Add(this, [
            ("devproxy cert ensure", "Install and trust certificate"),
            ("devproxy cert export", "Write root certificate to dev-proxy-ca.pem"),
            ("devproxy cert export --format crt --file ca.crt", "Write root certificate as PEM to ca.crt"),
            ("devproxy cert remove --force", "Remove certificate (no prompt)"),
        ]);
    }

    private int EnsureCert()
    {
        _logger.LogTrace("EnsureCert() called");

        try
        {
            // Resolving the shared root certificate creates + persists it on first run.
            _logger.LogInformation("Ensuring certificate exists and is trusted...");
            _rootCertificateTrust.Trust(_rootCertificate.Value);
            _logger.LogInformation("DONE");
            return 0;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error ensuring certificate");
            return 1;
        }
        finally
        {
            _logger.LogTrace("EnsureCert() finished");
        }
    }

    public int RemoveCert(ParseResult parseResult)
    {
        _logger.LogTrace("RemoveCert() called");

        try
        {
            var isForced = parseResult.GetValue(_forceOption);
            if (!isForced)
            {
                if (Console.IsInputRedirected ||
                    Environment.GetEnvironmentVariable("CI") is not null)
                {
                    _logger.LogError("Confirmation required but running in non-interactive mode. Use --force to skip confirmation.");
                    return 1;
                }

                var isConfirmed = PromptConfirmation("Do you want to remove the root certificate", acceptByDefault: false);
                if (!isConfirmed)
                {
                    return 0;
                }
            }

            _logger.LogInformation("Uninstalling the root certificate...");

            _rootCertificateTrust.Untrust(_rootCertificate.Value);

            _logger.LogInformation("DONE");
            return 0;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error removing certificate");
            return 1;
        }
        finally
        {
            _logger.LogTrace("RemoveCert() finished");
        }
    }

    public int ExportCert(ParseResult parseResult)
    {
        // stdout is reserved for the exported file path, which scripts capture
        _logRouting?.RouteAllToStandardError();
        _logger.LogTrace("ExportCert() called");

        var outputFormat = parseResult.GetValueOrDefault<OutputFormat?>(DevProxyCommand.OutputOptionName) ?? OutputFormat.Text;

        try
        {
            if (!_certificateAuthority.Value.IsRootPersisted)
            {
                // An unsaved root is replaced on the next run, so an exported copy wouldn't be trusted.
                _logger.LogError("The root certificate couldn't be saved to disk, so it would change on the next Dev Proxy run. Fix the permissions of the Dev Proxy configuration folder (or DEV_PROXY_CERT_PATH) and try again.");
                return 1;
            }

            var file = parseResult.GetValue(_exportFileOption);
            var format = ResolveExportFormat(parseResult.GetValue(_exportFormatOption), file);
            var filePath = Path.GetFullPath(string.IsNullOrWhiteSpace(file)
                ? GetDefaultExportFileName(format)
                : ProxyUtils.ReplacePathTokens(file));

            if (IsSameFile(filePath, _certificateAuthority.Value.RootCertificatePath))
            {
                _logger.LogError("Can't export the certificate to {Path} because it would overwrite the stored root certificate and its private key. Choose a different file.", filePath);
                return 1;
            }

            var directory = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(directory))
            {
                _ = Directory.CreateDirectory(directory);
            }

            File.WriteAllBytes(filePath, GetCertificateBytes(_rootCertificate.Value, format));

            var output = parseResult.InvocationConfiguration.Output;
            if (outputFormat == OutputFormat.Json)
            {
                var json = JsonSerializer.Serialize(new
                {
                    path = filePath,
                    format = format.ToString().ToLowerInvariant()
                }, ProxyUtils.JsonSerializerOptions);
                output.WriteLine(json);
            }
            else
            {
                output.WriteLine(filePath);
            }

            return 0;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error exporting certificate");
            return 1;
        }
        finally
        {
            _logger.LogTrace("ExportCert() finished");
        }
    }

    internal static CertExportFormat ResolveExportFormat(CertExportFormat? format, string? file)
    {
        if (format is not null)
        {
            return format.Value;
        }

        return Path.GetExtension(file ?? string.Empty).ToLowerInvariant() switch
        {
            ".crt" => CertExportFormat.Crt,
            ".der" => CertExportFormat.Der,
            _ => CertExportFormat.Pem
        };
    }

    internal static bool IsSameFile(string path1, string path2)
    {
        // Paths that differ only in case may or may not be the same file depending on the
        // volume's case sensitivity, so let the file system decide below.
        if (string.Equals(Path.GetFullPath(path1), Path.GetFullPath(path2), StringComparison.Ordinal))
        {
            return true;
        }

        if (!File.Exists(path1) || !File.Exists(path2))
        {
            return false;
        }

        // Different paths can reference the same file through symbolic or hard links
        // (e.g. /tmp and /private/tmp on macOS). While path1 is open exclusively, opening
        // the same file through any other path fails with a sharing violation (Windows)
        // or a lock conflict (Unix), while an independent copy opens normally.
        using var exclusive = new FileStream(path1, FileMode.Open, FileAccess.Read, FileShare.None);
        try
        {
            using var other = new FileStream(path2, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return false;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException or UnauthorizedAccessException)
        {
            return false;
        }
        catch (IOException)
        {
            return true;
        }
    }

    internal static string GetDefaultExportFileName(CertExportFormat format) =>
        $"{DefaultExportFileName}.{format.ToString().ToLowerInvariant()}";

    internal static byte[] GetCertificateBytes(X509Certificate2 certificate, CertExportFormat format)
    {
        ArgumentNullException.ThrowIfNull(certificate);

        return format == CertExportFormat.Der
            ? certificate.Export(X509ContentType.Cert)
            : Encoding.ASCII.GetBytes(certificate.ExportCertificatePem() + "\n");
    }

    private static bool PromptConfirmation(string message, bool acceptByDefault)
    {
        while (true)
        {
            Console.Write(message + $" ({(acceptByDefault ? "Y/n" : "y/N")}): ");
            var answer = Console.ReadLine();

            if (string.IsNullOrWhiteSpace(answer))
            {
                return acceptByDefault;
            }
            else if (string.Equals("y", answer, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            else if (string.Equals("n", answer, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }
    }
}