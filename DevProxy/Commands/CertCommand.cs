// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Proxy;
using System.CommandLine;
using System.CommandLine.Parsing;
using Titanium.Web.Proxy.Helpers;

namespace DevProxy.Commands;

sealed class CertCommand : Command
{
    private readonly ILogger _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly Option<bool> _forceOption = new("--force", "-f")
    {
        Description = "Don't prompt for confirmation when removing the certificate. Required for non-interactive use (CI, piped stdin, automation)."
    };

    public CertCommand(ILogger<CertCommand> logger, ILoggerFactory loggerFactory) :
        base("cert", "Manage the Dev Proxy certificate")
    {
        _logger = logger;
        _loggerFactory = loggerFactory;

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
    }

    private async Task EnsureCertAsync()
    {
        _logger.LogTrace("EnsureCertAsync() called");

        try
        {
            // Ensure ProxyServer is initialized with LoggerFactory for Unobtanium logging
            ProxyEngine.EnsureProxyServerInitialized(_loggerFactory);

            _logger.LogInformation("Ensuring certificate exists and is trusted...");
            await ProxyEngine.ProxyServer.CertificateManager.EnsureRootCertificateAsync();

            if (RunTime.IsMac)
            {
                var certificate = ProxyEngine.ProxyServer.CertificateManager.RootCertificate;
                if (certificate is not null)
                {
                    MacCertificateHelper.TrustCertificate(certificate, _logger);
                }
            }

            _logger.LogInformation("DONE");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error ensuring certificate");
        }

        _logger.LogTrace("EnsureCertAsync() finished");
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

            // Ensure ProxyServer is initialized with LoggerFactory for Unobtanium logging
            ProxyEngine.EnsureProxyServerInitialized(_loggerFactory);

            if (RunTime.IsMac)
            {
                var certificate = ProxyEngine.ProxyServer.CertificateManager.RootCertificate;
                if (certificate is not null)
                {
                    MacCertificateHelper.RemoveTrustedCertificate(certificate, _logger);
                }

                HasRunFlag.Remove();
            }
            else
            {
                ProxyEngine.ProxyServer.CertificateManager.RemoveTrustedRootCertificate(machineTrusted: false);
            }

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