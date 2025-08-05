// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy;
using DevProxy.Abstractions.Data;
using DevProxy.Abstractions.LanguageModel;
using DevProxy.Abstractions.Proxy;
using DevProxy.Commands;
using DevProxy.Proxy;

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
            .AddSingleton((IConfigurationRoot)configuration)
            .AddSingleton<IProxyConfiguration, ProxyConfiguration>()
            .AddSingleton<IProxyStateController, ProxyStateController>()
            .AddSingleton<IProxyState, ProxyState>()
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