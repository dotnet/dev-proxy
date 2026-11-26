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
        DevProxyConfigOptions options,
        string[] allowedOrigins)
    {
        _ = services.AddControllers();
        _ = services.AddCors(options =>
        {
            options.AddDefaultPolicy(builder =>
            {
                _ = builder
                    .WithOrigins(allowedOrigins)
                    .WithMethods("GET", "POST")
                    .WithHeaders("Authorization", "Content-Type", "Accept");
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
        _ = services.AddSingleton<SystemProxyManager>();
        // Route through the process-wide session (when registered) so the system proxy
        // stays registered across hot-reload restarts.
        _ = services.AddSingleton<ISystemProxyManager>(sp =>
        {
            var manager = sp.GetRequiredService<SystemProxyManager>();
            return sp.GetService<SystemProxySession>()?.Track(manager) ?? manager;
        });
        _ = services.AddKestrelCertificateAuthority();
        _ = services.AddHostedService(sp => new KestrelProxyEngine(
            sp.GetRequiredService<CertificateAuthority>(),
            sp.GetServices<IPlugin>(),
            sp.GetRequiredService<ISet<UrlToWatch>>(),
            sp.GetRequiredService<IProxyConfiguration>(),
            sp.GetRequiredService<IProxyState>().GlobalData,
            sp.GetRequiredService<ILoggerFactory>(),
            sp.GetRequiredService<IRootCertificateTrust>(),
            sp.GetRequiredService<ISystemProxyManager>(),
            sp.GetRequiredService<IHostApplicationLifetime>()));

        return services;
    }

    // Startup requests (schema validation, update check) run before the listener binds, so
    // keep them off a system proxy registration retained from the previous run.
    internal static IServiceCollection AddRetainedSystemProxyBypass(this IServiceCollection services) =>
        services.ConfigureHttpClientDefaults(builder =>
            builder.ConfigurePrimaryHttpMessageHandler((handler, sp) =>
            {
                if (sp.GetService<SystemProxySession>() is not { } session)
                {
                    return;
                }

                switch (handler)
                {
                    case SocketsHttpHandler { UseProxy: true, Proxy: null } socketsHandler:
                        socketsHandler.Proxy = new RetainedSystemProxyBypass(HttpClient.DefaultProxy, session);
                        break;
                    case HttpClientHandler { UseProxy: true, Proxy: null } httpClientHandler:
                        httpClientHandler.Proxy = new RetainedSystemProxyBypass(HttpClient.DefaultProxy, session);
                        break;
                    default:
                        break;
                }
            }));

    static IServiceCollection AddApplicationServices(
        this IServiceCollection services,
        ConfigurationManager configuration,
        DevProxyConfigOptions options)
    {
        _ = services
            .AddSingleton(options)
            .AddSingleton((IConfigurationRoot)configuration)
            .AddSingleton<IProxyConfiguration, ProxyConfiguration>()
            .AddSingleton<IProxyStateController, ProxyStateController>()
            .AddSingleton<IProxyState, ProxyState>()
            .AddSingleton<ISystemConsole, SystemConsole>()
            .AddHostedService<ConfigFileWatcher>()
            // Share the hosted instance so the command can release its startup-output gate.
            .AddSingleton<InteractiveConsoleService>()
            .AddHostedService(sp => sp.GetRequiredService<InteractiveConsoleService>())
            .AddSingleton(sp => LanguageModelClientFactory.Create(sp, configuration))
            .AddSingleton<UpdateNotification>()
            .AddSingleton<DevProxyCommand>()
            .AddSingleton<MSGraphDb>()
            .AddHttpClient()
            .AddRetainedSystemProxyBypass();

        _ = services.AddPlugins(configuration, options);

        return services;
    }
}