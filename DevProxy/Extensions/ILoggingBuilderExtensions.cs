// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Abstractions.Proxy;
using DevProxy.Commands;
using DevProxy.Logging;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Options;

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
        DevProxyConfigOptions options) =>
        builder.ConfigureDevProxyLogging(configuration, options, DevProxyCommand.IsStdioCommand, DevProxyCommand.IsJwtCommand, DevProxyCommand.IsRootCommand, DevProxyCommand.IsCertExportCommand);

    internal static ILoggingBuilder ConfigureDevProxyLogging(
        this ILoggingBuilder builder,
        ConfigurationManager configuration,
        DevProxyConfigOptions options,
        bool isStdioCommand,
        bool isJwtCommand,
        bool isRootCommand,
        bool isCertExportCommand = false)
    {
        // Registered for every command so that commands can depend on it. cert export is detected
        // early when possible, so that diagnostics logged before it runs also go to stderr.
#pragma warning disable CA2000 // Lives for the whole process
        var logRouting = new ConsoleLogRouting(allToStandardError: isCertExportCommand);
#pragma warning restore CA2000
        _ = builder.Services
            .AddSingleton(logRouting)
            .AddSingleton<IOptionsChangeTokenSource<ConsoleLoggerOptions>>(logRouting);

        var configuredLogLevel = options.LogLevel ??
            configuration.GetValue("logLevel", LogLevel.Information);

        // Determine the output format (text or json)
        var configuredOutput = options.Output ??
            configuration.GetValue("output", OutputFormat.Text);

        // For stdio command, log to file instead of console to avoid interfering with proxied streams
        if (isStdioCommand)
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

        // For jwt command, suppress all logging to console to avoid interfering with token output.
        // JSON output is emitted through the logger, so it uses the regular subcommand logging.
        if (isJwtCommand && configuredOutput != OutputFormat.Json)
        {
            _ = builder
                .ClearProviders()
                .SetMinimumLevel(LogLevel.None);
            return builder;
        }

        var showSkipMessages = configuration.GetValue("showSkipMessages", true);
        // Detached files need request timestamps even when interactive timestamps are hidden.
        var showTimestamps = DevProxyCommand.IsInternalDaemon || configuration.GetValue("showTimestamps", true);

        // Select the appropriate formatter based on output setting
        var formatterName = configuredOutput == OutputFormat.Json
            ? JsonConsoleFormatter.FormatterName
            : ProxyConsoleFormatter.DefaultCategoryName;

        // For root command (proxy itself), use rich logging
        if (isRootCommand)
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
                .AddConsole(consoleOptions =>
                    {
                        consoleOptions.FormatterName = formatterName;
                        consoleOptions.LogToStandardErrorThreshold = logRouting.GetStandardErrorThreshold(LogLevel.Warning);
                    }
                )
                .AddConsoleFormatter<ProxyConsoleFormatter, ProxyConsoleFormatterOptions>(formatterOptions =>
                    {
                        formatterOptions.IncludeScopes = true;
                        formatterOptions.ShowSkipMessages = showSkipMessages;
                        formatterOptions.ShowTimestamps = showTimestamps;
                        formatterOptions.IncludeFullTimestamps = DevProxyCommand.IsInternalDaemon;
                    }
                )
                .AddConsoleFormatter<JsonConsoleFormatter, ProxyConsoleFormatterOptions>(formatterOptions =>
                    {
                        formatterOptions.IncludeScopes = true;
                        formatterOptions.ShowSkipMessages = showSkipMessages;
                        formatterOptions.ShowTimestamps = showTimestamps;
                    }
                )
                .AddRequestLogger()
                .SetMinimumLevel(configuredLogLevel);
            return builder;
        }

        // For other subcommands (cert, config, outdated, msgraphdb), use rich logging
        // but with plugin messages filtered out

        // Structured results are logged at Information. When JSON output is
        // requested with a higher log level, let Information through the
        // pipeline and have the JSON formatter drop diagnostic messages below
        // the configured level, so that result records are always emitted.
        var pipelineLogLevel = configuredOutput == OutputFormat.Json && configuredLogLevel > LogLevel.Information
            ? LogLevel.Information
            : configuredLogLevel;

        _ = builder
            .AddFilter("Microsoft.Hosting.*", LogLevel.Error)
            .AddFilter("Microsoft.AspNetCore.*", LogLevel.Error)
            .AddFilter("Microsoft.Extensions.*", LogLevel.Error)
            .AddFilter("System.*", LogLevel.Error)
            .AddFilter("DevProxy.Plugins.*", LogLevel.None)
            .AddConsole(consoleOptions =>
                {
                    consoleOptions.FormatterName = formatterName;
                    consoleOptions.LogToStandardErrorThreshold = logRouting.GetStandardErrorThreshold(LogLevel.Warning);
                }
            )
            .AddConsoleFormatter<ProxyConsoleFormatter, ProxyConsoleFormatterOptions>(formatterOptions =>
                {
                    formatterOptions.IncludeScopes = true;
                    formatterOptions.ShowSkipMessages = showSkipMessages;
                    formatterOptions.ShowTimestamps = showTimestamps;
                }
            )
            .AddConsoleFormatter<JsonConsoleFormatter, ProxyConsoleFormatterOptions>(formatterOptions =>
                {
                    formatterOptions.IncludeScopes = true;
                    formatterOptions.ShowSkipMessages = showSkipMessages;
                    formatterOptions.ShowTimestamps = showTimestamps;
                    formatterOptions.MinimumLogLevel = configuredLogLevel;
                }
            )
            .SetMinimumLevel(pipelineLogLevel);

        return builder;
    }
}