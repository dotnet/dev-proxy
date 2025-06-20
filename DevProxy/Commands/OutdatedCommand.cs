// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Abstractions.Proxy;
using System.CommandLine;

namespace DevProxy.Commands;

sealed class OutdatedCommand : Command
{
    private readonly IProxyConfiguration _proxyConfiguration;
    private readonly UpdateNotification _updateNotification;
    private readonly ILogger _logger;

    public OutdatedCommand(
        IProxyConfiguration proxyConfiguration,
        UpdateNotification updateNotification,
        ILogger<OutdatedCommand> logger) :
        base("outdated", "Check for new version")
    {
        _proxyConfiguration = proxyConfiguration;
        _updateNotification = updateNotification;
        _logger = logger;

        ConfigureCommand();
    }

    private void ConfigureCommand()
    {
        var outdatedShortOption = new Option<bool>("--short", "Return version only");
        AddOption(outdatedShortOption);
        this.SetHandler(CheckVersionAsync, outdatedShortOption);
    }

    private async Task CheckVersionAsync(bool versionOnly)
    {
        var releaseInfo = await _updateNotification.CheckForNewVersionAsync(_proxyConfiguration.NewVersionNotification);

        if (releaseInfo is not null && releaseInfo.Version is not null)
        {
            var isBeta = releaseInfo.Version.Contains("-beta", StringComparison.OrdinalIgnoreCase);

            if (versionOnly)
            {
                _logger.LogInformation("{Version}", releaseInfo.Version);
            }
            else
            {
                var notesLink = isBeta ? "https://aka.ms/devproxy/notes" : "https://aka.ms/devproxy/beta/notes";
                _logger.LogInformation(
                    "New Dev Proxy version {Version} is available.{NewLine}Release notes: {Link}{NewLine}Docs: https://aka.ms/devproxy/upgrade",
                    releaseInfo.Version,
                    Environment.NewLine,
                    notesLink,
                    Environment.NewLine
                );
            }
        }
        else if (!versionOnly)
        {
            _logger.LogInformation("You are using the latest version of Dev Proxy.");
        }
    }
}