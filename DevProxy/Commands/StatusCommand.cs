// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.State;
using System.CommandLine;
using System.Text.Json.Serialization;

namespace DevProxy.Commands;

internal sealed class StatusCommand : Command
{
    public StatusCommand() : base("status", "Show status of running Dev Proxy instance")
    {
        SetAction(RunAsync);
    }

    private async Task<int> RunAsync(ParseResult parseResult, CancellationToken cancellationToken)
    {
        var state = await StateManager.LoadStateAsync(cancellationToken);

        if (state == null)
        {
            Console.WriteLine("Dev Proxy is not running.");
            return 1;
        }

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
                Console.WriteLine($"  PID:        {state.Pid}");
                Console.WriteLine($"  API URL:    {state.ApiUrl}");
                Console.WriteLine($"  Port:       {state.Port}");
                Console.WriteLine($"  Recording:  {(proxyInfo?.Recording == true ? "Yes" : "No")}");
                if (!string.IsNullOrEmpty(state.ConfigFile))
                {
                    Console.WriteLine($"  Config:     {state.ConfigFile}");
                }
                Console.WriteLine($"  Log file:   {state.LogFile}");
                Console.WriteLine($"  Started:    {state.StartedAt.LocalDateTime:g}");

                return 0;
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
        Console.WriteLine($"  PID:        {state.Pid}");
        Console.WriteLine($"  API URL:    {state.ApiUrl}");
        Console.WriteLine($"  Log file:   {state.LogFile}");
        Console.WriteLine($"  Started:    {state.StartedAt.LocalDateTime:g}");

        return 0;
    }

    private sealed class ProxyStatusInfo
    {
        [JsonPropertyName("recording")]
        public bool? Recording { get; set; }

        [JsonPropertyName("configFile")]
        public string? ConfigFile { get; set; }
    }
}