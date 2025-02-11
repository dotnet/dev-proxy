// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Microsoft.DevProxy.Abstractions;
using Microsoft.VisualStudio.Threading;

namespace Microsoft.DevProxy.Logging;

public class RequestLogger(PluginEvents pluginEvents) : ILogger
{
    private readonly PluginEvents _pluginEvents = pluginEvents;

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => default;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (state is RequestLog requestLog)
        {
            var joinableTaskContext = new JoinableTaskContext();
            var joinableTaskFactory = new JoinableTaskFactory(joinableTaskContext);
            
            joinableTaskFactory.Run(async () => await _pluginEvents.RaiseRequestLoggedAsync(new RequestLogArgs(requestLog)));
        }
    }
}