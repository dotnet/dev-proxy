// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Abstractions.Proxy;
using DevProxy.Abstractions.Utils;
using DevProxy.State;
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

        var apiTokenCommand = new Command("token", """
            Print the API token of a running Dev Proxy instance.

            Examples:
              devproxy api token
              devproxy api token --pid 12345
              devproxy api token --output json

            Selects the only running instance. With multiple instances, specify --pid.
            Reads credentials for the current user; Dev Proxy must already be running.
            Prints the secret to stdout, including when redirected. Errors go to stderr.
            JSON output: { "pid": number, "apiUrl": string, "token": string }.
            Exit codes: 0 success, 1 instance/credential unavailable, 2 invalid arguments.
            """);
        var pidOption = new Option<int?>("--pid")
        {
            Description = "Retrieve the token of a specific Dev Proxy instance"
        };
        apiTokenCommand.Add(pidOption);
        apiTokenCommand.SetAction(async (parseResult, cancellationToken) =>
        {
            var outputFormat = parseResult.GetValueOrDefault<OutputFormat?>(DevProxyCommand.OutputOptionName) ?? OutputFormat.Text;
            return await PrintTokenAsync(parseResult.GetValue(pidOption), outputFormat, cancellationToken);
        });

        this.AddCommands(new List<Command>
        {
            apiShowCommand,
            apiTokenCommand
        }.OrderByName());
    }

    private static async Task<int> PrintTokenAsync(int? pid, OutputFormat outputFormat, CancellationToken cancellationToken)
    {
        try
        {
            ProxyInstanceState? state;
            if (pid.HasValue)
            {
                state = await StateManager.LoadStateByPidAsync(pid.Value, cancellationToken);
            }
            else
            {
                var states = await StateManager.LoadAllStatesAsync(cancellationToken);
                if (states.Count > 1)
                {
                    await Console.Error.WriteLineAsync("Multiple Dev Proxy instances are running. Select one with devproxy api token --pid <PID>:");
                    foreach (var instance in states.OrderBy(instance => instance.Pid))
                    {
                        await Console.Error.WriteLineAsync($"  {instance.Pid}: {instance.ApiUrl}");
                    }
                    return 1;
                }

                state = states.SingleOrDefault();
            }

            if (state is null)
            {
                await Console.Error.WriteLineAsync(pid.HasValue
                    ? $"No running Dev Proxy instance with PID {pid.Value}. Run devproxy status to find an instance."
                    : "Dev Proxy is not running. Start it with devproxy first.");
                return 1;
            }

            var token = await File.ReadAllTextAsync(ApiSecurity.GetTokenFilePath(state.Pid), cancellationToken);
            if (outputFormat == OutputFormat.Json)
            {
                Console.WriteLine(JsonSerializer.Serialize(new { pid = state.Pid, apiUrl = state.ApiUrl, token }, ProxyUtils.JsonSerializerOptions));
            }
            else
            {
                Console.WriteLine(token);
            }
            return 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await Console.Error.WriteLineAsync("Unable to read the API token. Restart the selected Dev Proxy instance using the current version and the same user account.");
            return 1;
        }
    }

    private void PrintApiInfo(OutputFormat outputFormat)
    {
        var apiPort = _proxyConfiguration.ApiPort;
        var baseUrl = ApiSecurity.GetApiUrl(new UriBuilder(Uri.UriSchemeHttp, _proxyConfiguration.ApiIpAddress, apiPort).Uri.AbsoluteUri);
        var tokenFilePattern = Path.Combine(StateManager.GetConfigFolder(), "credentials", "api-<PID>.token");

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
                authentication = new { scheme = "Bearer", header = "Authorization", tokenFilePattern },
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
            _logger.LogInformation("All endpoints require Authorization: Bearer <token>.");
            _logger.LogInformation("Get your token: devproxy api token (use --pid <PID> when multiple instances are running).");
            _logger.LogInformation("Use devproxy status to discover running instances and their actual API ports.");
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