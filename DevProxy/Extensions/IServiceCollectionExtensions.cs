// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy;
using DevProxy.Abstractions.Data;
using DevProxy.Abstractions.LanguageModel;
using DevProxy.Abstractions.Plugins;
using DevProxy.Abstractions.Proxy;
using DevProxy.Commands;
using DevProxy.Proxy;
using DevProxy.Proxy.Kestrel;
using Microsoft.Extensions.Logging;

#pragma warning disable IDE0130
namespace Microsoft.Extensions.DependencyInjection;
#pragma warning restore IDE0130

static class IServiceCollectionExtensions
{
    public static IServiceCollection ConfigureDevProxyServices(
        this IServiceCollection services,
        ConfigurationManager configuration,
        DevProxyConfigOptions options)
    {
        _ = services.AddControllers();
        _ = services.AddCors(options =>
        {
            options.AddDefaultPolicy(builder =>
            {
                _ = builder
                    .AllowAnyOrigin()
                    .AllowAnyMethod()
                    .AllowAnyHeader();
            });
        });
        _ = services
            .AddApplicationServices(configuration, options)
            .AddProxyEngine()
            .Configure<RouteOptions>(options => options.LowercaseUrls = true);

        return services;
    }

    // Engine selection (dev-toggle). The Titanium engine is the default; setting
    // DEV_PROXY_ENGINE=kestrel selects the new Kestrel engine so the two can run
    // side-by-side during development for golden-output comparison. This toggle is
    // NOT a shipped fallback — it is removed at the hard cut-over (decision #3).
    static IServiceCollection AddProxyEngine(this IServiceCollection services)
    {
        var engine = Environment.GetEnvironmentVariable("DEV_PROXY_ENGINE");
        if (string.Equals(engine, "kestrel", StringComparison.OrdinalIgnoreCase))
        {
            _ = services.AddSingleton<IRootCertificateTrust, RootCertificateTrust>();
            _ = services.AddHostedService(sp => new KestrelProxyEngine(
                sp.GetServices<IPlugin>(),
                sp.GetRequiredService<ISet<UrlToWatch>>(),
                sp.GetRequiredService<IProxyConfiguration>(),
                sp.GetRequiredService<IProxyState>().GlobalData,
                sp.GetRequiredService<ILoggerFactory>(),
                sp.GetRequiredService<IRootCertificateTrust>()));
        }
        else
        {
            _ = services.AddHostedService<ProxyEngine>();
        }

        return services;
    }

    static IServiceCollection AddApplicationServices(
        this IServiceCollection services,
        ConfigurationManager configuration,
        DevProxyConfigOptions options)
    {
        _ = services
            .AddSingleton((IConfigurationRoot)configuration)
            .AddSingleton<IProxyConfiguration, ProxyConfiguration>()
            .AddSingleton<IProxyStateController, ProxyStateController>()
            .AddSingleton<IProxyState, ProxyState>()
            .AddHostedService<ConfigFileWatcher>()
            .AddSingleton(sp => ProxyEngine.Certificate!)
            .AddSingleton(sp => LanguageModelClientFactory.Create(sp, configuration))
            .AddSingleton<UpdateNotification>()
            .AddSingleton<ProxyEngine>()
            .AddSingleton<DevProxyCommand>()
            .AddSingleton<MSGraphDb>()
            .AddHttpClient();

        _ = services.AddPlugins(configuration, options);

        return services;
    }
}