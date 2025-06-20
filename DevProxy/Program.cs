// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy;
using DevProxy.Commands;
using System.Net;

static WebApplication BuildApplication(string[] args)
{
    var builder = WebApplication.CreateBuilder(args);

    _ = builder.Configuration.ConfigureDevProxyConfig();
    _ = builder.Logging.ConfigureDevProxyLogging(builder.Configuration);
    _ = builder.Services.ConfigureDevProxyServices(builder.Configuration, args);

    var defaultIpAddress = "127.0.0.1";
    var ipAddress = DevProxyCommand.IPAddress ??
        builder.Configuration.GetValue("ipAddress", defaultIpAddress) ??
        defaultIpAddress;
    _ = builder.WebHost.ConfigureKestrel(options =>
    {
        var apiPort = builder.Configuration.GetValue("apiPort", 8897);
        options.Listen(IPAddress.Parse(ipAddress), apiPort);
    });

    var app = builder.Build();

    _ = app.UseSwagger();
    _ = app.UseSwaggerUI();
    _ = app.MapControllers();

    return app;
}
_ = Announcement.ShowAsync();

var app = BuildApplication(args);

var rootCommand = app.Services.GetRequiredService<DevProxyCommand>();
var loggerFactory = app.Services.GetRequiredService<ILoggerFactory>();

var exitCode = await rootCommand.InvokeAsync(args, app);
loggerFactory.Dispose();
Environment.Exit(exitCode);
