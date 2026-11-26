// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Abstractions.Proxy;
using DevProxy.Abstractions.Utils;
using DevProxy.Logging;
using DevProxy.Proxy;
using DevProxy.State;
using System.CommandLine;
using System.CommandLine.Parsing;
using System.Text.Json;

namespace DevProxy.Commands;

sealed class SystemProxyCommand : Command
{
    private readonly ILogger<SystemProxyManager> _systemProxyLogger;
    private readonly ILogger<SystemProxyCommand> _logger;
    private readonly Option<bool> _forceOption = new("--force", "-f")
    {
        Description = "Reset the system proxy even if a running Dev Proxy instance owns it"
    };

    public SystemProxyCommand(ILogger<SystemProxyManager> systemProxyLogger, ILogger<SystemProxyCommand> logger) :
        base("system-proxy", "Manage the system proxy settings changed by Dev Proxy")
    {
        _systemProxyLogger = systemProxyLogger;
        _logger = logger;

        var resetCommand = new Command("reset", "Restore the system proxy settings that Dev Proxy changed. Use after Dev Proxy was killed or crashed.");
        resetCommand.Add(_forceOption);
        resetCommand.SetAction(ResetAsync);
        Add(resetCommand);

        HelpExamples.Add(this, [
            ("devproxy system-proxy reset", "Restore system proxy settings"),
        ]);
        HelpExamples.Add(resetCommand, [
            ("devproxy system-proxy reset", "Restore system proxy settings"),
            ("devproxy system-proxy reset --force", "Restore even if Dev Proxy is running"),
        ]);
    }

    private Task<int> ResetAsync(ParseResult parseResult, CancellationToken cancellationToken) =>
        ResetCoreAsync(parseResult.GetValue(_forceOption),
            parseResult.GetValueOrDefault<OutputFormat?>(DevProxyCommand.OutputOptionName) ?? OutputFormat.Text,
            _logger, _systemProxyLogger, cancellationToken);

    /// <summary>
    /// Builds a minimal command tree mirroring the registered <c>system-proxy reset</c>
    /// command and the global options, so the standalone path parses arguments the same way.
    /// </summary>
    private static (RootCommand RootCommand, Option<bool> ForceOption) CreateStandaloneResetParser()
    {
        var forceOption = new Option<bool>("--force", "-f");
        var resetCommand = new Command("reset") { forceOption };
        var rootCommand = new RootCommand { new Command("system-proxy") { resetCommand } };
        rootCommand.Options.Add(new Option<OutputFormat?>(DevProxyCommand.OutputOptionName) { Recursive = true });
        rootCommand.Options.Add(new Option<LogLevel?>(DevProxyCommand.LogLevelOptionName) { Recursive = true });
        rootCommand.Options.Add(new Option<bool>(DevProxyCommand.NoColorOptionName) { Recursive = true, Arity = ArgumentArity.Zero });
        return (rootCommand, forceOption);
    }

    /// <summary>
    /// Returns whether the arguments (without the executable) invoke <c>system-proxy reset</c>,
    /// wherever global options appear and however their values are attached.
    /// </summary>
    internal static bool IsResetInvocation(string[] args)
    {
        var parseResult = CreateStandaloneResetParser().RootCommand.Parse(args);
        // Leave help (including help in a response file) to the full command tree.
        if (parseResult.Action is System.CommandLine.Help.HelpAction)
        {
            return false;
        }
        var commandResult = parseResult.CommandResult;
        return commandResult.Command.Name == "reset" &&
            commandResult.Parent is System.CommandLine.Parsing.CommandResult { Command.Name: "system-proxy" };
    }

    /// <summary>
    /// Runs <c>system-proxy reset</c> without building the application host, so a broken
    /// configuration or plugin can't prevent restoring network access.
    /// </summary>
    internal static async Task<int> RunResetStandaloneAsync(string[] args)
    {
        var (rootCommand, forceOption) = CreateStandaloneResetParser();
        var parseResult = rootCommand.Parse(args);
        var outputFormat = parseResult.GetValueOrDefault<OutputFormat?>(DevProxyCommand.OutputOptionName) ?? OutputFormat.Text;
        var logLevel = parseResult.GetValueOrDefault<LogLevel?>(DevProxyCommand.LogLevelOptionName) ?? LogLevel.Information;

        using var loggerFactory = LoggerFactory.Create(builder =>
        {
            _ = builder
                .SetMinimumLevel(logLevel)
                .AddConsole(consoleOptions =>
                {
                    consoleOptions.FormatterName = outputFormat == OutputFormat.Json
                        ? JsonConsoleFormatter.FormatterName
                        : ProxyConsoleFormatter.DefaultCategoryName;
                    consoleOptions.LogToStandardErrorThreshold = LogLevel.Warning;
                })
                .AddConsoleFormatter<ProxyConsoleFormatter, ProxyConsoleFormatterOptions>(formatterOptions =>
                {
                    formatterOptions.IncludeScopes = false;
                    formatterOptions.ShowTimestamps = false;
                })
                .AddConsoleFormatter<JsonConsoleFormatter, ProxyConsoleFormatterOptions>(formatterOptions =>
                {
                    formatterOptions.IncludeScopes = false;
                    formatterOptions.ShowTimestamps = false;
                });
        });
        var logger = loggerFactory.CreateLogger<SystemProxyCommand>();
        if (parseResult.Errors.Count > 0)
        {
            foreach (var error in parseResult.Errors)
            {
                logger.LogError("{Message}", error.Message);
            }
            return 2;
        }

        return await ResetCoreAsync(parseResult.GetValue(forceOption), outputFormat, logger,
            loggerFactory.CreateLogger<SystemProxyManager>(), CancellationToken.None);
    }

    private static async Task<int> ResetCoreAsync(bool force, OutputFormat outputFormat,
        ILogger<SystemProxyCommand> logger, ILogger<SystemProxyManager> systemProxyLogger, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsMacOS())
        {
            LogResult("Dev Proxy doesn't change the system proxy settings on this operating system. Nothing to reset.", outputFormat, logger);
            return 0;
        }

        var liveOwner = await SystemProxyManager.FindLiveSystemProxyOwnerAsync(cancellationToken);
        if (liveOwner is not null && !force)
        {
            logger.LogError("Dev Proxy (PID: {Pid}) is running as the system proxy.", liveOwner);
            logger.LogError("Use 'devproxy stop' to stop it, or 'devproxy system-proxy reset --force' to reset anyway.");
            return 1;
        }

        var result = new SystemProxyManager(systemProxyLogger).Restore();
        if (result is SystemProxyRestoreResult.Failed)
        {
            logger.LogError("Couldn't restore the system proxy settings. To reset them manually, see https://go.devproxy.net?go=proxy-reset.");
            return 1;
        }

        // A forced reset took the system proxy away from live detached instances. Record
        // that, so stopping them or recovering after they crash doesn't change the
        // restored settings again.
        foreach (var state in (await StateManager.LoadAllStatesAsync(cancellationToken)).Where(s => s.AsSystemProxy))
        {
            state.AsSystemProxy = false;
            await StateManager.SaveStateAsync(state, cancellationToken);
        }

        // The settings are restored, so records of dead instances that registered as the
        // system proxy are stale. Remove them so they don't trigger another restore later.
        foreach (var orphan in await StateManager.GetOrphanedSystemProxyStatesAsync(cancellationToken))
        {
            await StateManager.DeleteStateAsync(orphan, cancellationToken);
        }

        LogResult(result is SystemProxyRestoreResult.RestoredPrevious
            ? "Restored the system proxy settings from before Dev Proxy started."
            : "Turned off the system proxy.", outputFormat, logger);
        return 0;
    }

    private static void LogResult(string message, OutputFormat outputFormat, ILogger<SystemProxyCommand> logger)
    {
        if (outputFormat == OutputFormat.Json)
        {
            logger.LogStructuredOutput(JsonSerializer.Serialize(new { message }, ProxyUtils.JsonSerializerOptions));
        }
        else
        {
            logger.LogInformation("{Message}", message);
        }
    }
}
