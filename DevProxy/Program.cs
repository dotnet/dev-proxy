// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy;
using DevProxy.Commands;
using DevProxy.Proxy;
using System.Net;

static WebApplication BuildApplication(DevProxyConfigOptions options)
{
    // Don't pass command-line args to WebApplication.CreateBuilder because:
    // 1. Dev Proxy uses System.CommandLine for CLI parsing, not ASP.NET Core's CommandLineConfigurationProvider
    // 2. ConfigureDevProxyConfig clears all configuration sources anyway and only uses JSON config files
    var builder = WebApplication.CreateBuilder();

    _ = builder.Configuration.ConfigureDevProxyConfig(options);
    _ = builder.Logging.ConfigureDevProxyLogging(builder.Configuration, options);
    _ = builder.Services.ConfigureDevProxyServices(builder.Configuration, options);

    var defaultIpAddress = "127.0.0.1";
    var ipAddress = options.IPAddress ??
        builder.Configuration.GetValue("ipAddress", defaultIpAddress) ??
        defaultIpAddress;
    var defaultApiPort = 8897;
    var apiPort = options.ApiPort ??
        builder.Configuration.GetValue("apiPort", defaultApiPort);
    _ = builder.WebHost.ConfigureKestrel(options =>
    {
        options.Listen(IPAddress.Parse(ipAddress), apiPort);
    });

    var app = builder.Build();

    _ = app.UseCors();
    _ = app.UseSwagger();
    _ = app.UseSwaggerUI();
    _ = app.MapControllers();

    return app;
}

static async Task<int> RunProxyAsync(string[] args, DevProxyConfigOptions options)
{
    var app = BuildApplication(options);
    try
    {
        var devProxyCommand = app.Services.GetRequiredService<DevProxyCommand>();
        return await devProxyCommand.InvokeAsync(args, app);
    }
    finally
    {
        // Dispose the app to clean up all services (including FileSystemWatchers in BaseLoader)
        await app.DisposeAsync();
    }
}

_ = Announcement.ShowAsync();

var options = new DevProxyConfigOptions();
options.ParseOptions(args);

int exitCode;
bool shouldRestart;
do
{
    try
    {
        // Reset the restart flag before each run
        ConfigFileWatcher.Reset();
        exitCode = await RunProxyAsync(args, options);

        // Wait for proxy to fully stop (including system proxy deregistration)
        // before starting the new instance
        if (ConfigFileWatcher.ProxyStoppedCompletionSource is not null)
        {
            var proxyStoppedTask = ConfigFileWatcher.ProxyStoppedCompletionSource.Task;
            var timeoutTask = Task.Delay(TimeSpan.FromSeconds(30));
#pragma warning disable VSTHRD003 // Intentionally waiting for external signal
            var completedTask = await Task.WhenAny(proxyStoppedTask, timeoutTask);
#pragma warning restore VSTHRD003

            // If the timeout elapses before the proxy signals it has stopped,
            // continue to avoid hanging the restart loop indefinitely
            if (completedTask == proxyStoppedTask)
            {
#pragma warning disable VSTHRD003 // Observe exceptions from completed task
                await proxyStoppedTask;
#pragma warning restore VSTHRD003
            }
        }

        shouldRestart = ConfigFileWatcher.IsRestarting;
    }
    catch (Exception ex)
    {
        await Console.Error.WriteLineAsync("Unhandled exception during proxy run. Stopping restart loop.");
        await Console.Error.WriteLineAsync(ex.ToString());
        exitCode = 1;
        shouldRestart = false;
    }
} while (shouldRestart);

Environment.Exit(exitCode);
