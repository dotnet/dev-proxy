// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy;
using DevProxy.Commands;
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

    _ = app.UseSwagger();
    _ = app.UseSwaggerUI();
    _ = app.MapControllers();

    return app;
}
_ = Announcement.ShowAsync();

var options = new DevProxyConfigOptions();
options.ParseOptions(args);
var app = BuildApplication(options);

var devProxyCommand = app.Services.GetRequiredService<DevProxyCommand>();
var loggerFactory = app.Services.GetRequiredService<ILoggerFactory>();

var exitCode = await devProxyCommand.InvokeAsync(args, app);
loggerFactory.Dispose();
Environment.Exit(exitCode);
