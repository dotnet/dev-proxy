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
using DevProxy.Proxy.Kestrel.Internal;
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

    // The Kestrel engine is the sole proxy engine. It receives the shared
    // CertificateAuthority (registered via AddKestrelCertificateAuthority) plus the
    // host's platform trust + system-proxy implementations.
    static IServiceCollection AddProxyEngine(this IServiceCollection services)
    {
        _ = services.AddSingleton<IRootCertificateTrust, RootCertificateTrust>();
        _ = services.AddSingleton<ISystemProxyManager, SystemProxyManager>();
        _ = services.AddKestrelCertificateAuthority();
        _ = services.AddHostedService(sp => new KestrelProxyEngine(
            sp.GetRequiredService<CertificateAuthority>(),
            sp.GetServices<IPlugin>(),
            sp.GetRequiredService<ISet<UrlToWatch>>(),
            sp.GetRequiredService<IProxyConfiguration>(),
            sp.GetRequiredService<IProxyState>().GlobalData,
            sp.GetRequiredService<ILoggerFactory>(),
            sp.GetRequiredService<IRootCertificateTrust>(),
            sp.GetRequiredService<ISystemProxyManager>()));

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
            .AddSingleton<ISystemConsole, SystemConsole>()
            .AddHostedService<ConfigFileWatcher>()
            .AddHostedService<InteractiveConsoleService>()
            .AddSingleton(sp => LanguageModelClientFactory.Create(sp, configuration))
            .AddSingleton<UpdateNotification>()
            .AddSingleton<DevProxyCommand>()
            .AddSingleton<MSGraphDb>()
            .AddHttpClient();

        _ = services.AddPlugins(configuration, options);

        return services;
    }
}