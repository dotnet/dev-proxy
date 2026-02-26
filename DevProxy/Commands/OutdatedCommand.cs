// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Abstractions.Proxy;
using DevProxy.Abstractions.Utils;
using System.CommandLine;
using System.CommandLine.Parsing;
using System.Text.Json;

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
        var outdatedShortOption = new Option<bool>("--short")
        {
            Description = "Return version only",
        };
        Add(outdatedShortOption);

        HelpExamples.Add(this, [
            "devproxy outdated                                   Check for updates (human output)",
            "devproxy outdated --short                           Version number only",
        ]);

        SetAction(async (parseResult) =>
        {
            var versionOnly = parseResult.GetValue(outdatedShortOption);
            var outputFormat = parseResult.GetValueOrDefault<OutputFormat?>(DevProxyCommand.OutputOptionName) ?? OutputFormat.Text;
            await CheckVersionAsync(versionOnly, outputFormat);
        });
    }

    private async Task CheckVersionAsync(bool versionOnly, OutputFormat outputFormat)
    {
        var releaseInfo = await _updateNotification.CheckForNewVersionAsync(_proxyConfiguration.NewVersionNotification);

        if (outputFormat == OutputFormat.Json)
        {
            WriteJsonOutput(releaseInfo, versionOnly);
            return;
        }

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

    private void WriteJsonOutput(ReleaseInfo? releaseInfo, bool versionOnly)
    {
        if (releaseInfo is not null && releaseInfo.Version is not null)
        {
            var isBeta = releaseInfo.Version.Contains("-beta", StringComparison.OrdinalIgnoreCase);
            var notesLink = isBeta ? "https://aka.ms/devproxy/notes" : "https://aka.ms/devproxy/beta/notes";

            if (versionOnly)
            {
                var json = JsonSerializer.Serialize(new
                {
                    version = releaseInfo.Version
                }, ProxyUtils.JsonSerializerOptions);
                _logger.LogStructuredOutput(json);
            }
            else
            {
                var json = JsonSerializer.Serialize(new
                {
                    version = releaseInfo.Version,
                    current = ProxyUtils.ProductVersion,
                    releaseNotes = notesLink,
                    upgradeUrl = "https://aka.ms/devproxy/upgrade"
                }, ProxyUtils.JsonSerializerOptions);
                _logger.LogStructuredOutput(json);
            }
        }
        else
        {
            var json = JsonSerializer.Serialize(new
            {
                current = ProxyUtils.ProductVersion
            }, ProxyUtils.JsonSerializerOptions);
            _logger.LogStructuredOutput(json);
        }
    }
}