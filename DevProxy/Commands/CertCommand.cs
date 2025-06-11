// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Abstractions.Utils;
using DevProxy.Proxy;
using System.CommandLine;
using System.CommandLine.Invocation;
using System.CommandLine.Parsing;
using System.Diagnostics;
using Titanium.Web.Proxy.Helpers;

namespace DevProxy.Commands;

sealed class CertCommand : Command
{
    private readonly ILogger _logger;
    private readonly Option<bool> _forceOption = new(["--force", "-f"], "Force the root certificate removal");

    public CertCommand(ILogger<CertCommand> logger) :
        base("cert", "Manage the Dev Proxy certificate")
    {
        _logger = logger;

        ConfigureCommand();
    }

    private void ConfigureCommand()
    {
        var certEnsureCommand = new Command("ensure", "Ensure certificates are setup (creates root if required). Also makes root certificate trusted.");
        certEnsureCommand.SetHandler(EnsureCertAsync);

        var certRemoveCommand = new Command("remove", "Remove the certificate from Root Store");
        certRemoveCommand.SetHandler(RemoveCert);
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
            _logger.LogInformation("Ensuring certificate exists and is trusted...");
            await ProxyEngine.ProxyServer.CertificateManager.EnsureRootCertificateAsync();
            _logger.LogInformation("DONE");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error ensuring certificate");
        }

        _logger.LogTrace("EnsureCertAsync() finished");
    }

    public void RemoveCert(InvocationContext invocationContext)
    {
        _logger.LogTrace("RemoveCert() called");
        ArgumentNullException.ThrowIfNull(_logger);
        ArgumentNullException.ThrowIfNull(invocationContext);
        ArgumentNullException.ThrowIfNull(_forceOption);

        try
        {
            var isForced = invocationContext.ParseResult.GetValueForOption(_forceOption);
            if (!isForced)
            {
                var isConfirmed = PromptConfirmation("Do you want to remove the root certificate", defaultValue: false);
                if (!isConfirmed)
                {
                    return;
                }
            }

            _logger.LogInformation("Uninstalling the root certificate...");

            RemoveTrustedCertificateOnMac();
            ProxyEngine.ProxyServer.CertificateManager.RemoveTrustedRootCertificate(machineTrusted: false);

            _logger.LogInformation("DONE");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error removing certificate");
        }
        finally
        {
            _logger.LogTrace("RemoveCert() finished");
        }
    }

    private static bool PromptConfirmation(string message, bool defaultValue)
    {
        while (true)
        {
            Console.Write(message + $" ({(defaultValue ? "Y/n" : "y/N")}): ");
            var answer = Console.ReadLine();

            if (string.IsNullOrWhiteSpace(answer))
            {
                return defaultValue;
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

    private static void RemoveTrustedCertificateOnMac()
    {
        if (!RunTime.IsMac)
        {
            return;
        }

        var bashScriptPath = Path.Join(ProxyUtils.AppFolder, "remove-cert.sh");
        ProcessStartInfo startInfo = new()
        {
            FileName = "/bin/bash",
            Arguments = bashScriptPath,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var process = new Process() { StartInfo = startInfo };
        _ = process.Start();
        process.WaitForExit();
    }
}