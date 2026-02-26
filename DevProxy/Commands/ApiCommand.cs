// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Abstractions.Proxy;
using DevProxy.Abstractions.Utils;
using Microsoft.Extensions.Logging;
using System.CommandLine;
using System.CommandLine.Parsing;
using System.Text.Json;

namespace DevProxy.Commands;

sealed class ApiCommand : Command
{
    private readonly IProxyConfiguration _proxyConfiguration;
    private readonly ILogger _logger;

    public ApiCommand(IProxyConfiguration proxyConfiguration, ILogger<ApiCommand> logger) :
        base("api", "Manage Dev Proxy API information")
    {
        _proxyConfiguration = proxyConfiguration;
        _logger = logger;
        ConfigureCommand();
    }

    private void ConfigureCommand()
    {
        var apiShowCommand = new Command("show", "Display Dev Proxy API information for runtime management");
        apiShowCommand.SetAction(parseResult =>
        {
            var outputFormat = parseResult.GetValueOrDefault<OutputFormat?>(DevProxyCommand.OutputOptionName) ?? OutputFormat.Text;
            PrintApiInfo(outputFormat);
        });

        this.AddCommands(new List<Command>
        {
            apiShowCommand
        }.OrderByName());
    }

    private void PrintApiInfo(OutputFormat outputFormat)
    {
        var ipAddress = _proxyConfiguration.IPAddress;
        var apiPort = _proxyConfiguration.ApiPort;
        var baseUrl = $"http://{ipAddress}:{apiPort}";

        var endpoints = new[]
        {
            new ApiEndpointInfo { Method = "GET", Path = "/proxy", Description = "Get proxy status" },
            new ApiEndpointInfo { Method = "POST", Path = "/proxy", Description = "Update proxy status (e.g. start/stop recording)" },
            new ApiEndpointInfo { Method = "POST", Path = "/proxy/mockRequest", Description = "Issue a mock request" },
            new ApiEndpointInfo { Method = "POST", Path = "/proxy/stopProxy", Description = "Stop the proxy" },
            new ApiEndpointInfo { Method = "POST", Path = "/proxy/jwtToken", Description = "Create a JWT token" },
            new ApiEndpointInfo { Method = "GET", Path = "/proxy/rootCertificate", Description = "Get the root certificate" },
            new ApiEndpointInfo { Method = "GET", Path = "/proxy/logs", Description = "Get proxy logs (for detached mode access)" }
        };

        if (outputFormat == OutputFormat.Json)
        {
            var json = JsonSerializer.Serialize(new
            {
                baseUrl,
                swaggerUrl = $"{baseUrl}/swagger/v1/swagger.json",
                endpoints = endpoints.Select(e => new
                {
                    method = e.Method,
                    path = e.Path,
                    description = e.Description
                })
            }, ProxyUtils.JsonSerializerOptions);
            _logger.LogStructuredOutput(json);
        }
        else
        {
            _logger.LogInformation("Base URL: {BaseUrl}", baseUrl);
            _logger.LogInformation("OpenAPI spec: {SwaggerUrl}", $"{baseUrl}/swagger/v1/swagger.json");
            _logger.LogInformation("");
            _logger.LogInformation("Endpoints:");
            foreach (var endpoint in endpoints)
            {
                _logger.LogInformation("  {Method,-6} {Path,-30} {Description}", endpoint.Method, endpoint.Path, endpoint.Description);
            }
        }
    }
}

sealed class ApiEndpointInfo
{
    public string Method { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
}
