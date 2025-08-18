// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy;
using DevProxy.Commands;
using System.Net;
using Unobtanium.Web.Proxy;
using Unobtanium.Web.Proxy.Services;

static WebApplication BuildApplication(string[] args, DevProxyConfigOptions options)
{
    var builder = WebApplication.CreateBuilder(args);

    _ = builder.Configuration.ConfigureDevProxyConfig(options);
    _ = builder.Logging.ConfigureDevProxyLogging(builder.Configuration, options);
    _ = builder.Services.ConfigureDevProxyServices(builder.Configuration, options);
    _ = builder.Services
        .Configure<ProxyServerOptions>(options =>
        {
            options.Port = ProxyServerDefaults.DEFAULT_PORT; // Set the port for the proxy server
            options.HttpsPort = ProxyServerDefaults.DEFAULT_HTTPS_PORT;
        })
        .Configure<CertificateManagerConfiguration>(config =>
        {
            config.CachePath = DevProxy.Abstractions.Utils.ProxyUtils.ReplacePathTokens(
                builder.Configuration.GetValue("certificateCachePath", "certs"));
        })
        .AddProxyEvents(new Unobtanium.Web.Proxy.Events.ProxyServerEvents())
        .AddProxyServices();

    var defaultIpAddress = "127.0.0.1";
    var ipAddress = options.IPAddress ??
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

var options = new DevProxyConfigOptions();
options.ParseOptions(args);
var app = BuildApplication(args, options);

var devProxyCommand = app.Services.GetRequiredService<DevProxyCommand>();
var loggerFactory = app.Services.GetRequiredService<ILoggerFactory>();

var exitCode = await devProxyCommand.InvokeAsync(args, app);
loggerFactory.Dispose();
Environment.Exit(exitCode);
