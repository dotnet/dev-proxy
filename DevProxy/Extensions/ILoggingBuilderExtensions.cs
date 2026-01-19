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
        ConfigurationManager configuration,
        DevProxyConfigOptions options)
    {
        var configuredLogLevel = options.LogLevel ??
            configuration.GetValue("logLevel", LogLevel.Information);

        // For stdio command, log to file instead of console to avoid interfering with proxied streams
        if (DevProxyCommand.IsStdioCommand)
        {
            _ = builder
                .ClearProviders()
                .SetMinimumLevel(configuredLogLevel);
#pragma warning disable CA2000 // Dispose objects before losing scope - DI container manages lifetime
            _ = builder.Services.AddSingleton<ILoggerProvider>(
                new StdioFileLoggerProvider(DevProxyCommand.StdioLogFilePath));
#pragma warning restore CA2000
            return builder;
        }

        // For jwt command, suppress all logging to console to avoid interfering with token output
        if (DevProxyCommand.IsJwtCommand)
        {
            _ = builder
                .ClearProviders()
                .SetMinimumLevel(LogLevel.None);
            return builder;
        }

        // For root command (proxy itself), use rich logging
        if (DevProxyCommand.IsRootCommand)
        {
            _ = builder
                .AddFilter("Microsoft.Hosting.*", LogLevel.Error)
                .AddFilter("Microsoft.AspNetCore.*", LogLevel.Error)
                .AddFilter("Microsoft.Extensions.*", LogLevel.Error)
                .AddFilter("System.*", LogLevel.Error)
                // Only show plugin messages when no global options are set
                .AddFilter("DevProxy.Plugins.*", level =>
                    level >= configuredLogLevel &&
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

        // For other subcommands (cert, config, outdated, msgraphdb), use simple console logging
        // with plugin messages filtered out
        _ = builder
            .ClearProviders()
            .AddSimpleConsole(options =>
            {
                options.SingleLine = true;
                options.IncludeScopes = false;
            })
            .AddFilter("Microsoft.Hosting.*", LogLevel.None)
            .AddFilter("Microsoft.AspNetCore.*", LogLevel.None)
            .AddFilter("Microsoft.Extensions.*", LogLevel.None)
            .AddFilter("System.*", LogLevel.None)
            .AddFilter("DevProxy.Plugins.*", LogLevel.None)
            .SetMinimumLevel(configuredLogLevel);

        return builder;
    }
}
