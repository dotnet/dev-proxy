// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Abstractions.Proxy;
using DevProxy.Abstractions.Utils;
using DevProxy.State;
using System.CommandLine;
using System.CommandLine.Parsing;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DevProxy.Commands;

internal sealed class StatusCommand : Command
{
    private readonly ILogger<StatusCommand> _logger;
    private readonly Option<int?> _pidOption = new("--pid")
    {
        Description = "Show status of a specific Dev Proxy instance by PID"
    };

    public StatusCommand(ILogger<StatusCommand> logger) : base("status", """
                Show status of running Dev Proxy instances.

                Examples:
                    devproxy status
                    devproxy status --pid 12345
                    devproxy status --output json

                Uses the current user's instance credentials. Tokens are included unless CI is set.
                JSON output: one result event per instance, with pid, apiUrl, token, apiStatus,
                port, recording, asSystemProxy, configFile, logFile and startedAt.
                An empty result has running: false. Token is omitted in CI.
                Exit codes: 0 = matching instance found; 1 = no matching instance.
                API errors are reported in the output without changing the instance's running status.
                """)
    {
        _logger = logger;
        Add(_pidOption);
        SetAction(RunAsync);
    }

    private async Task<int> RunAsync(ParseResult parseResult, CancellationToken cancellationToken)
    {
        var pid = parseResult.GetValue(_pidOption);
        var outputFormat = parseResult.GetValueOrDefault<OutputFormat?>(DevProxyCommand.OutputOptionName) ?? OutputFormat.Text;

        if (pid is not null)
        {
            var state = await StateManager.LoadStateByPidAsync(pid.Value, cancellationToken);
            if (state is null)
            {
                PrintNoInstances(outputFormat, pid);
                return 1;
            }

            await PrintInstanceStatusAsync(state, outputFormat, cancellationToken);
            return 0;
        }

        var states = await StateManager.LoadAllStatesAsync(cancellationToken);
        if (states.Count == 0)
        {
            PrintNoInstances(outputFormat, null);
            return 1;
        }

        for (var i = 0; i < states.Count; i++)
        {
            if (i > 0 && outputFormat == OutputFormat.Text)
            {
                Console.WriteLine();
            }

            await PrintInstanceStatusAsync(states[i], outputFormat, cancellationToken);
        }

        return 0;
    }

    private void PrintNoInstances(OutputFormat outputFormat, int? pid)
    {
        if (outputFormat == OutputFormat.Json)
        {
            _logger.LogStructuredOutput(JsonSerializer.Serialize(new { running = false, pid }, ProxyUtils.JsonSerializerOptions));
        }
        else
        {
            Console.WriteLine(pid is null ? "Dev Proxy is not running." : $"No running Dev Proxy instance with PID {pid}.");
        }
    }

    private async Task PrintInstanceStatusAsync(ProxyInstanceState state, OutputFormat outputFormat, CancellationToken cancellationToken)
    {
        ProxyStatusInfo? proxyInfo = null;
        string? token = null;
        var apiStatus = "unavailable";
        var message = "Dev Proxy appears to be running (API not responding).";
        try
        {
            using var httpClient = await ApiSecurity.CreateClientAsync(state, TimeSpan.FromSeconds(5), cancellationToken);
            token = ApiSecurity.ShouldDisplayToken ? httpClient.DefaultRequestHeaders.Authorization!.Parameter : null;
            using var response = await httpClient.GetAsync("/proxy", cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                proxyInfo = await response.Content.ReadFromJsonAsync<ProxyStatusInfo>(cancellationToken: cancellationToken);
                apiStatus = "available";
                message = "Dev Proxy is running.";
            }
            else if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                apiStatus = "unauthorized";
                message = "Dev Proxy is running, but API authentication failed. The stored token was rejected; restart this instance to regenerate its credential.";
            }
        }
        catch (FormatException)
        {
            apiStatus = "invalidCredentials";
            message = "The stored API token is malformed. Restart this instance to regenerate its credential.";
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
        }
        catch (TaskCanceledException)
        {
        }

        if (outputFormat == OutputFormat.Json)
        {
            _logger.LogStructuredOutput(JsonSerializer.Serialize(new
            {
                state.Pid,
                state.ApiUrl,
                token,
                apiStatus,
                message,
                state.Port,
                recording = proxyInfo?.Recording,
                state.AsSystemProxy,
                state.ConfigFile,
                state.LogFile,
                state.StartedAt
            }, ProxyUtils.JsonSerializerOptions));
            return;
        }

        Console.WriteLine(message);
        Console.WriteLine();
        Console.WriteLine($"  PID:              {state.Pid}");
        Console.WriteLine($"  API URL:          {state.ApiUrl}");
        if (token is not null)
        {
            Console.WriteLine($"  API token:        {token}");
        }
        Console.WriteLine($"  Port:             {state.Port}");
        Console.WriteLine($"  System proxy:     {(state.AsSystemProxy ? "Yes" : "No")}");
        if (proxyInfo is not null)
        {
            Console.WriteLine($"  Recording:        {(proxyInfo.Recording == true ? "Yes" : "No")}");
        }
        if (!string.IsNullOrEmpty(state.ConfigFile))
        {
            Console.WriteLine($"  Config:           {state.ConfigFile}");
        }
        Console.WriteLine($"  Log file:         {state.LogFile}");
        Console.WriteLine($"  Started:          {state.StartedAt.LocalDateTime:g}");
    }

    private sealed class ProxyStatusInfo
    {
        [JsonPropertyName("recording")]
        public bool? Recording { get; set; }

        [JsonPropertyName("configFile")]
        public string? ConfigFile { get; set; }
    }
}