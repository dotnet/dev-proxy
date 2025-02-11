// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Microsoft.DevProxy.Abstractions;
using Microsoft.DevProxy.Logging;

#pragma warning disable IDE0130
namespace Microsoft.Extensions.Logging;
#pragma warning restore IDE0130

public static class ILoggerBuilderExtensions
{
    public static ILoggingBuilder AddRequestLogger(this ILoggingBuilder builder, PluginEvents pluginEvents)
    {
        builder.Services.AddSingleton<ILoggerProvider, RequestLoggerProvider>(provider =>
        {
            return new RequestLoggerProvider(pluginEvents);
        });

        return builder;
    }
}
