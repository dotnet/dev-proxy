// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.State;
using System.CommandLine;
using System.Text.Json.Serialization;

namespace DevProxy.Commands;

internal sealed class StatusCommand : Command
{
    private readonly Option<int?> _pidOption = new("--pid")
    {
        Description = "Show status of a specific Dev Proxy instance by PID"
    };

    public StatusCommand() : base("status", "Show status of running Dev Proxy instances")
    {
        Add(_pidOption);
        SetAction(RunAsync);
    }

    private async Task<int> RunAsync(ParseResult parseResult, CancellationToken cancellationToken)
    {
        var pid = parseResult.GetValue(_pidOption);

        if (pid is not null)
        {
            var state = await StateManager.LoadStateByPidAsync(pid.Value, cancellationToken);
            if (state is null)
            {
                Console.WriteLine($"No running Dev Proxy instance with PID {pid.Value}.");
                return 1;
            }

            await PrintInstanceStatusAsync(state, cancellationToken);
            return 0;
        }

        var states = await StateManager.LoadAllStatesAsync(cancellationToken);
        if (states.Count == 0)
        {
            Console.WriteLine("Dev Proxy is not running.");
            return 1;
        }

        for (var i = 0; i < states.Count; i++)
        {
            if (i > 0)
            {
                Console.WriteLine();
            }

            await PrintInstanceStatusAsync(states[i], cancellationToken);
        }

        return 0;
    }

    private static async Task PrintInstanceStatusAsync(ProxyInstanceState state, CancellationToken cancellationToken)
    {
        // Try to get live status from the API
        try
        {
            using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            var response = await httpClient.GetAsync($"{state.ApiUrl}/proxy", cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                var proxyInfo = await response.Content.ReadFromJsonAsync<ProxyStatusInfo>(cancellationToken: cancellationToken);

                Console.WriteLine("Dev Proxy is running.");
                Console.WriteLine();
                Console.WriteLine($"  PID:              {state.Pid}");
                Console.WriteLine($"  API URL:          {state.ApiUrl}");
                Console.WriteLine($"  Port:             {state.Port}");
                Console.WriteLine($"  System proxy:     {(state.AsSystemProxy ? "Yes" : "No")}");
                Console.WriteLine($"  Recording:        {(proxyInfo?.Recording == true ? "Yes" : "No")}");
                if (!string.IsNullOrEmpty(state.ConfigFile))
                {
                    Console.WriteLine($"  Config:           {state.ConfigFile}");
                }
                Console.WriteLine($"  Log file:         {state.LogFile}");
                Console.WriteLine($"  Started:          {state.StartedAt.LocalDateTime:g}");

                return;
            }
        }
        catch (HttpRequestException)
        {
            // API not responding - instance might be starting up or crashed
        }
        catch (TaskCanceledException)
        {
            // Timeout - instance might be busy
        }

        // Fall back to state file info
        Console.WriteLine("Dev Proxy appears to be running (API not responding).");
        Console.WriteLine();
        Console.WriteLine($"  PID:              {state.Pid}");
        Console.WriteLine($"  API URL:          {state.ApiUrl}");
        Console.WriteLine($"  System proxy:     {(state.AsSystemProxy ? "Yes" : "No")}");
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