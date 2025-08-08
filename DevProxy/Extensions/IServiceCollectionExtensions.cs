// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy;
using DevProxy.Abstractions.Data;
using DevProxy.Abstractions.LanguageModel;
using DevProxy.Abstractions.Proxy;
using DevProxy.Commands;
using DevProxy.Proxy;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Unobtanium.Web.Proxy;
using Unobtanium.Web.Proxy.Models;

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
            .AddProxyConfiguration(configuration)
            .AddSingleton<ProxyServer>() // ProxyServer has to be injected
            .AddSingleton((IConfigurationRoot)configuration)
            .AddSingleton<IProxyConfiguration, ProxyConfiguration>()
            .AddSingleton<IProxyStateController, ProxyStateController>()
            .AddSingleton<IProxyState, ProxyState>()
            // TODO: Removed the injected certificate
            //.AddSingleton(sp => ProxyEngine.Certificate!) // Why is this injected?
            .AddSingleton(sp => sp.GetRequiredService<ProxyServer>().CertificateManager.RootCertificate!)
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
        _ = services.AddTransient<IProxyServerHttpClientFactory, EfficientProxyHttpClientFactory>();
        return services;
    }

    static IServiceCollection AddProxyConfiguration(this IServiceCollection services, IConfigurationRoot configuration)
    {
        var ipAddressString = configuration.GetValue<string?>("IPAddress");
        var ipAddress = string.IsNullOrEmpty(ipAddressString) ? System.Net.IPAddress.Any : System.Net.IPAddress.Parse(ipAddressString);
        var proxyConfig = new ProxyServerConfiguration
        {
            TcpTimeWaitSeconds = 10,
            ConnectionTimeOutSeconds = 10,
            ReuseSocket = false,
            EnableConnectionPool = true,
            ForwardToUpstreamGateway = true,
            CertificateTrustMode = ProxyCertificateTrustMode.UserTrust,
            // Default endpoint? Load port from config?
            EndPoints = [new ExplicitProxyEndPoint(ipAddress, configuration.GetValue("Port", 8000))],
            CertificateCacheFolder = configuration.GetValue<string?>("DEV_PROXY_CERT_PATH"), // By default the configuration also has environment variables. Loading this from config makes it more flexible.
            RootCertificateName = "Dev Proxy CA",
        };
        _ = services.AddSingleton(proxyConfig);
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
                        .AddSource(ProxyServerDefaults.ActivitySourceName)
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