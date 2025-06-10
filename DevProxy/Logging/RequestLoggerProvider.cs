// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Proxy;
using System.Collections.Concurrent;

namespace DevProxy.Logging;

sealed class RequestLoggerProvider(IServiceProvider serviceProvider) : ILoggerProvider
{
    private readonly ConcurrentDictionary<string, RequestLogger> _loggers = new();
    private readonly IServiceProvider _serviceProvider = serviceProvider;

    public ILogger CreateLogger(string categoryName)
    {
        return _loggers.GetOrAdd(categoryName, name =>
        {
            var proxyState = _serviceProvider.GetRequiredService<IProxyState>();
            return new RequestLogger(_serviceProvider, proxyState);
        });
    }

    public void Dispose()
    {
        _loggers.Clear();
    }
}