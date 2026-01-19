// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Abstractions.Proxy;
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

        // Determine the log target audience (human or machine)
        var configuredLogFor = options.LogFor ??
            configuration.GetValue("logFor", LogFor.Human);

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

        var showSkipMessages = configuration.GetValue("showSkipMessages", true);
        var showTimestamps = configuration.GetValue("showTimestamps", true);

        // Select the appropriate formatter based on logFor setting
        var formatterName = configuredLogFor == LogFor.Machine
            ? MachineConsoleFormatter.FormatterName
            : ProxyConsoleFormatter.DefaultCategoryName;

        _ = builder
            .AddFilter("Microsoft.Hosting.*", LogLevel.Error)
            .AddFilter("Microsoft.AspNetCore.*", LogLevel.Error)
            .AddFilter("Microsoft.Extensions.*", LogLevel.Error)
            .AddFilter("System.*", LogLevel.Error)
            // Only show plugin messages when no global options are set
            .AddFilter("DevProxy.Plugins.*", level =>
                level >= configuredLogLevel &&
                !DevProxyCommand.HasGlobalOptions)
            .AddConsole(consoleOptions =>
                {
                    consoleOptions.FormatterName = formatterName;
                    consoleOptions.LogToStandardErrorThreshold = LogLevel.Warning;
                }
            )
            .AddConsoleFormatter<ProxyConsoleFormatter, ProxyConsoleFormatterOptions>(formatterOptions =>
                {
                    formatterOptions.IncludeScopes = true;
                    formatterOptions.LogFor = configuredLogFor;
                    formatterOptions.ShowSkipMessages = showSkipMessages;
                    formatterOptions.ShowTimestamps = showTimestamps;
                }
            )
            .AddConsoleFormatter<MachineConsoleFormatter, ProxyConsoleFormatterOptions>(formatterOptions =>
                {
                    formatterOptions.IncludeScopes = true;
                    formatterOptions.LogFor = configuredLogFor;
                    formatterOptions.ShowSkipMessages = showSkipMessages;
                    formatterOptions.ShowTimestamps = showTimestamps;
                }
            )
            .AddRequestLogger()
            .SetMinimumLevel(configuredLogLevel);

        return builder;
    }
}
