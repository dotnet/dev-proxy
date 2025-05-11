// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Proxy;
using System.CommandLine;
using System.CommandLine.Parsing;

namespace DevProxy.Commands;

sealed class CertCommand : Command
{
    private readonly ILogger _logger;

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

        this.AddCommands(new List<Command>
        {
            certEnsureCommand
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
}