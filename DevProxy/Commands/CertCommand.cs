// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Abstractions.Proxy;
using DevProxy.Proxy;
using System.CommandLine;
using System.CommandLine.Parsing;
using System.Security.Cryptography.X509Certificates;

namespace DevProxy.Commands;

sealed class CertCommand : Command
{
    private readonly ILogger _logger;
    private readonly X509Certificate2 _rootCertificate;
    private readonly IRootCertificateTrust _rootCertificateTrust;
    private readonly Option<bool> _forceOption = new("--force", "-f")
    {
        Description = "Don't prompt for confirmation when removing the certificate. Required for non-interactive use (CI, piped stdin, automation)."
    };

    public CertCommand(
        ILogger<CertCommand> logger,
        X509Certificate2 rootCertificate,
        IRootCertificateTrust rootCertificateTrust) :
        base("cert", "Manage the Dev Proxy certificate")
    {
        _logger = logger;
        _rootCertificate = rootCertificate;
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

        this.AddCommands(new List<Command>
        {
            certEnsureCommand,
            certRemoveCommand,
        }.OrderByName());

        HelpExamples.Add(this, [
            "devproxy cert ensure                                Install and trust certificate",
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