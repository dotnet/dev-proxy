// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Commands;
using DevProxy.Logging;

#pragma warning disable IDE0130
namespace Microsoft.Extensions.Logging;
#pragma warning restore IDE0130

static class ILoggingBuilderExtensions
{
    public static ILoggingBuilder AddRequestLogger(this ILoggingBuilder builder)
    {
        _ = builder.Services.AddSingleton<ILoggerProvider, RequestLoggerProvider>();

        return builder;
    }

    public static ILoggingBuilder ConfigureDevProxyLogging(
        this ILoggingBuilder builder,
        ConfigurationManager configuration)
    {
        var configuredLogLevel = DevProxyCommand.LogLevel ??
            configuration.GetValue("logLevel", LogLevel.Information);

        _ = builder
            .AddFilter("Microsoft.Hosting.*", LogLevel.Error)
            .AddFilter("Microsoft.AspNetCore.*", LogLevel.Error)
            .AddFilter("Microsoft.Extensions.*", LogLevel.Error)
            .AddFilter("System.*", LogLevel.Error)
            // Only show plugin messages for the root command
            .AddFilter("DevProxy.Plugins.*", level =>
                level >= configuredLogLevel &&
                DevProxyCommand.IsRootCommand &&
                !DevProxyCommand.HasGlobalOptions)
            .AddConsole(options =>
                {
                    options.FormatterName = ProxyConsoleFormatter.DefaultCategoryName;
                    options.LogToStandardErrorThreshold = LogLevel.Warning;
                }
             )
            .AddConsoleFormatter<ProxyConsoleFormatter, ProxyConsoleFormatterOptions>(options =>
                {
                    options.IncludeScopes = true;
                    options.ShowSkipMessages = configuration.GetValue("showSkipMessages", true);
                    options.ShowTimestamps = configuration.GetValue("showTimestamps", true);
                }
            )
            .AddRequestLogger()
            .SetMinimumLevel(configuredLogLevel);

        return builder;
    }
}
