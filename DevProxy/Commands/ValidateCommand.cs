// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Abstractions.Utils;
using DevProxy.Abstractions.Proxy;
using System.CommandLine;
using System.CommandLine.Parsing;
using System.Net.Http.Headers;

namespace DevProxy.Commands;

/// <summary>
/// Top-level shortcut for 'devproxy config validate'. Validates a configuration
/// file without starting the proxy.
/// </summary>
sealed class ValidateCommand : Command
{
    private readonly ILogger _logger;
    private readonly HttpClient _httpClient;

    public ValidateCommand(
        HttpClient httpClient,
        ILogger<ValidateCommand> logger) :
        base("validate", "Validate a Dev Proxy configuration file without starting the proxy (same as 'config validate')")
    {
        _logger = logger;
        _httpClient = httpClient;
        if (_httpClient.DefaultRequestHeaders.UserAgent.Count == 0)
        {
            _httpClient.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("dev-proxy", ProxyUtils.ProductVersion));
        }
        ConfigureCommand();
    }

    private void ConfigureCommand()
    {
        var configFileOption = ConfigCommand.CreateValidateConfigFileOption();
        Add(configFileOption);
        SetAction(async (parseResult, cancellationToken) =>
        {
            var configFile = parseResult.GetValue(configFileOption);
            var output = parseResult.GetValueOrDefault<OutputFormat?>(DevProxyCommand.OutputOptionName);
            return await ConfigCommand.ValidateConfigCoreAsync(configFile, output == OutputFormat.Json, _httpClient, _logger, cancellationToken);
        });

        HelpExamples.Add(this, [
            "devproxy validate                                   Validate the default config",
            "devproxy validate -c myconfig.json                  Validate a specific config file",
            "devproxy validate -c myconfig.json --output json    Validate and output JSON results",
        ]);
    }
}
