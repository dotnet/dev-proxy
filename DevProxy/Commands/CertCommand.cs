// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Abstractions.Proxy;
using DevProxy.Abstractions.Utils;
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
    private readonly X509Certificate2 _rootCertificate;
    private readonly CertificateAuthority _certificateAuthority;
    private readonly IRootCertificateTrust _rootCertificateTrust;
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
        X509Certificate2 rootCertificate,
        CertificateAuthority certificateAuthority,
        IRootCertificateTrust rootCertificateTrust) :
        base("cert", "Manage the Dev Proxy certificate")
    {
        _logger = logger;
        _rootCertificate = rootCertificate;
        _certificateAuthority = certificateAuthority;
        _rootCertificateTrust = rootCertificateTrust;

        ConfigureCommand();
    }

    private void ConfigureCommand()
    {
        var certEnsureCommand = new Command("ensure", "Ensure certificates are setup (creates root if required). Also makes root certificate trusted.");
        certEnsureCommand.SetAction(async _ => await EnsureCertAsync());

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
            "devproxy cert ensure                                Install and trust certificate",
            "devproxy cert export                                Write root certificate to dev-proxy-ca.pem",
            "devproxy cert export --format crt --file ca.crt     Write root certificate as PEM to ca.crt",
            "devproxy cert remove --force                        Remove certificate (no prompt)",
        ]);
    }

    private Task EnsureCertAsync()
    {
        _logger.LogTrace("EnsureCertAsync() called");

        try
        {
            // Resolving the shared root certificate creates + persists it on first run.
            _logger.LogInformation("Ensuring certificate exists and is trusted...");
            _rootCertificateTrust.Trust(_rootCertificate);
            _logger.LogInformation("DONE");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error ensuring certificate");
        }

        _logger.LogTrace("EnsureCertAsync() finished");
        return Task.CompletedTask;
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

            _rootCertificateTrust.Untrust(_rootCertificate);

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
        _logger.LogTrace("ExportCert() called");

        var outputFormat = parseResult.GetValueOrDefault<OutputFormat?>(DevProxyCommand.OutputOptionName) ?? OutputFormat.Text;

        try
        {
            if (!_certificateAuthority.IsRootPersisted)
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

            var directory = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(directory))
            {
                _ = Directory.CreateDirectory(directory);
            }

            File.WriteAllBytes(filePath, GetCertificateBytes(_rootCertificate, format));

            if (outputFormat == OutputFormat.Json)
            {
                var json = JsonSerializer.Serialize(new
                {
                    path = filePath,
                    format = format.ToString().ToLowerInvariant()
                }, ProxyUtils.JsonSerializerOptions);
                Console.WriteLine(json);
            }
            else
            {
                Console.WriteLine(filePath);
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