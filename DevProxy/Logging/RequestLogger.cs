// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Abstractions.Plugins;
using DevProxy.Abstractions.Proxy;
using DevProxy.Proxy;
using Microsoft.VisualStudio.Threading;

namespace DevProxy.Logging;

sealed class RequestLogger(IServiceProvider serviceProvider, IProxyState proxyState) : ILogger
{
    private readonly IProxyState _proxyState = proxyState;
    private readonly IServiceProvider _serviceProvider = serviceProvider;

    public bool IsEnabled(LogLevel logLevel) => true;

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => default;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (state is RequestLog requestLog)
        {
            if (_proxyState.IsRecording)
            {
                _proxyState.RequestLogs.Add(requestLog);
            }

            var requestLogArgs = new RequestLogArgs(requestLog);

            using var joinableTaskContext = new JoinableTaskContext();
            var joinableTaskFactory = new JoinableTaskFactory(joinableTaskContext);

            // Lazily resolve plugins to avoid circular dependency
            var plugins = _serviceProvider.GetRequiredService<IEnumerable<IPlugin>>();

            foreach (var plugin in plugins.Where(p => p.Enabled))
            {
                joinableTaskFactory.Run(async () => await plugin.AfterRequestLogAsync(requestLogArgs));
            }
        }
    }
}