// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Abstractions.Proxy;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DevProxy.Integration.Tests;

/// <summary>
/// Minimal dependency-injection host for plugins whose <c>InitializeAsync</c>
/// constructs file loaders via <c>ActivatorUtilities.CreateInstance&lt;…Loader&gt;</c>.
/// Those loaders (subclasses of <c>BaseLoader</c>) resolve <see cref="HttpClient"/>,
/// <see cref="ILogger{T}"/>, and <see cref="IProxyConfiguration"/> from the service
/// provider, so the harness must register exactly those.
///
///   InitArgs.ServiceProvider ──► HttpClient
///                            ├──► ILoggerFactory / ILogger&lt;T&gt;
///                            └──► IProxyConfiguration
/// </summary>
internal sealed class PluginTestHost : IDisposable
{
    private readonly ServiceProvider _provider;

    public PluginTestHost(IProxyConfiguration proxyConfiguration, ILoggerFactory? loggerFactory = null)
    {
        ArgumentNullException.ThrowIfNull(proxyConfiguration);

        var services = new ServiceCollection();
        _ = services.AddSingleton(loggerFactory ?? NullLoggerFactory.Instance);
        _ = services.AddSingleton(typeof(ILogger<>), typeof(Logger<>));
        _ = services.AddSingleton(proxyConfiguration);
        _ = services.AddSingleton(TestDefaults.HttpClient);

        _provider = services.BuildServiceProvider();
    }

    public IServiceProvider Services => _provider;

    public InitArgs CreateInitArgs() => new() { ServiceProvider = _provider };

    public void Dispose() => _provider.Dispose();
}
