// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy;
using DevProxy.Abstractions.Data;
using DevProxy.Abstractions.LanguageModel;
using DevProxy.Abstractions.Plugins;
using DevProxy.Abstractions.Proxy;
using DevProxy.Commands;
using DevProxy.Plugins;
using DevProxy.Proxy;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Unobtanium.Web.Proxy;
using Unobtanium.Web.Proxy.Services;

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
        _ = services
            .AddOpenTelemetryConfig(configuration)
            .AddApplicationServices(configuration, options)
            .AddHostedService<ProxyEngine>()
            .AddEndpointsApiExplorer()
            .AddSwaggerGen()
            .Configure<RouteOptions>(options => options.LowercaseUrls = true);

        return services;
    }

    static IServiceCollection AddApplicationServices(
        this IServiceCollection services,
        ConfigurationManager configuration,
        DevProxyConfigOptions options)
    {
        _ = services
            .AddProxyHttpClientFactory()
            .AddProxyEvents(new Unobtanium.Web.Proxy.Events.ProxyServerEvents())
            .Configure<ProxyServerOptions>(options =>
            {
                options.Port = configuration.GetValue("Port", ProxyServerDefaults.DEFAULT_PORT);
                //options.TrustCertificateOnStart = true; // Automatically trust the certificate on start, NOT IMPLEMENTED YET!!
                options.TrustCertificateOnStartAsUser = true; // Automatically trust the certificate on start as user, NOT IMPLEMENTED YET!!
            })
            .Configure<CertificateManagerConfiguration> (certOptions =>
            {
                certOptions.RootCertificateName = "Dev Proxy CA";
                certOptions.CachePath = configuration.GetValue<string?>("DEV_PROXY_CERT_PATH");
            })
            .AddProxyServices() // This adds the background services for the proxy and adds the default ICertificateManager
            .AddSingleton((IConfigurationRoot)configuration)
            .AddSingleton<IProxyConfiguration, ProxyConfiguration>()
            .AddSingleton<IProxyStateController, ProxyStateController>()
            .AddSingleton<IProxyState, ProxyState>()
            .AddSingleton<IProxyStorage, ProxyStorage>()
            // TODO: Removed the injected certificate
            //.AddSingleton(sp => ProxyEngine.Certificate!) // Why is this injected?
            //.AddSingleton(sp => sp.GetRequiredService<ProxyServer>().CertificateManager.RootCertificate!)
            .AddSingleton(sp => LanguageModelClientFactory.Create(sp, configuration))
            .AddSingleton<UpdateNotification>()
            .AddSingleton<ProxyEngine>()
            .AddSingleton<DevProxyCommand>()
            .AddSingleton<MSGraphDb>()
            .AddHttpClient();

        _ = services.AddPlugins(configuration, options);

        return services;
    }

    static IServiceCollection AddProxyHttpClientFactory(this IServiceCollection services)
    {
        _ = services.AddHttpClient(EfficientProxyHttpClientFactory.HTTP_CLIENT_NAME, client =>
        {
            // Configure the HttpClient as needed, e.g., set base address, default headers, etc.
            //client.BaseAddress = new Uri("https://graph.microsoft.com/");
        })
        .ConfigurePrimaryHttpMessageHandler(() =>
        {
            // Configure HttpClientHandler to explicitly bypass system proxy settings
            return new HttpClientHandler()
            {
                UseProxy = false, // Explicitly disable proxy usage
                Proxy = null      // Ensure no proxy is set
            };
        });
        _ = services.AddTransient<IProxyHttpClientFactory, EfficientProxyHttpClientFactory>();
        return services;
    }

    static IServiceCollection AddOpenTelemetryConfig(this IServiceCollection services, IConfigurationRoot configuration)
    {
        var openTelemetryBuilder = services
            .AddOpenTelemetry()
                .WithMetrics(metrics =>
                {
                    _ = metrics
                        .AddHttpClientInstrumentation()
                        .AddRuntimeInstrumentation();

                }).WithTracing(tracing =>
                {
                    _ = tracing
                        .AddSource(ProxyServerDefaults.ACTIVITY_SOURCE_NAME)
                        .AddSource(ProxyEngine.ACTIVITY_SOURCE_NAME)
                        .AddHttpClientInstrumentation()
                        ;
                });
        var endpoint = configuration.GetValue<string?>("OTEL_EXPORTER_OTLP_ENDPOINT");
        if (!string.IsNullOrEmpty(endpoint))
        {
            _ = openTelemetryBuilder.UseOtlpExporter();
        }
        return services;
    }
}