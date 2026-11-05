// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Abstractions.Proxy;
using DevProxy.Logging;
using DevProxy.Proxy;
using DevProxy.State;
using System.CommandLine;

namespace DevProxy.Commands;

sealed class SystemProxyCommand : Command
{
    private readonly ILogger<SystemProxyManager> _systemProxyLogger;
    private readonly Option<bool> _forceOption = new("--force", "-f")
    {
        Description = "Reset the system proxy even if a running Dev Proxy instance owns it"
    };

    public SystemProxyCommand(ILogger<SystemProxyManager> systemProxyLogger) :
        base("system-proxy", "Manage the system proxy settings changed by Dev Proxy")
    {
        _systemProxyLogger = systemProxyLogger;

        var resetCommand = new Command("reset", "Restore the system proxy settings that Dev Proxy changed. Use after Dev Proxy was killed or crashed.");
        resetCommand.Add(_forceOption);
        resetCommand.SetAction(ResetAsync);
        Add(resetCommand);

        HelpExamples.Add(this, [
            "devproxy system-proxy reset                         Restore system proxy settings",
        ]);
        HelpExamples.Add(resetCommand, [
            "devproxy system-proxy reset                         Restore system proxy settings",
            "devproxy system-proxy reset --force                 Restore even if Dev Proxy is running",
        ]);
    }

    private Task<int> ResetAsync(ParseResult parseResult, CancellationToken cancellationToken) =>
        ResetCoreAsync(parseResult.GetValue(_forceOption), _systemProxyLogger, cancellationToken);

    /// <summary>
    /// Runs <c>system-proxy reset</c> without building the application host, so a broken
    /// configuration or plugin can't prevent restoring network access.
    /// </summary>
    internal static async Task<int> RunResetStandaloneAsync(string[] args)
    {
        // Parse with a minimal command tree mirroring the registered command so values
        // like '--force false' and invalid arguments are handled the same way.
        var forceOption = new Option<bool>("--force", "-f");
        var resetCommand = new Command("reset") { forceOption };
        var rootCommand = new RootCommand { new Command("system-proxy") { resetCommand } };
        rootCommand.Options.Add(new Option<OutputFormat?>(DevProxyCommand.OutputOptionName) { Recursive = true });
        rootCommand.Options.Add(new Option<LogLevel?>(DevProxyCommand.LogLevelOptionName) { Recursive = true });
        rootCommand.Options.Add(new Option<bool>(DevProxyCommand.NoColorOptionName) { Recursive = true, Arity = ArgumentArity.Zero });

        var parseResult = rootCommand.Parse(args);
        if (parseResult.Errors.Count > 0)
        {
            foreach (var error in parseResult.Errors)
            {
                await Console.Error.WriteLineAsync(error.Message);
            }
            return 1;
        }
        var force = parseResult.GetValue(forceOption);

        using var loggerFactory = LoggerFactory.Create(builder =>
        {
            _ = builder
                .SetMinimumLevel(LogLevel.Information)
                .AddConsole(consoleOptions =>
                {
                    consoleOptions.FormatterName = ProxyConsoleFormatter.DefaultCategoryName;
                    consoleOptions.LogToStandardErrorThreshold = LogLevel.Warning;
                })
                .AddConsoleFormatter<ProxyConsoleFormatter, ProxyConsoleFormatterOptions>(formatterOptions =>
                {
                    formatterOptions.IncludeScopes = false;
                    formatterOptions.ShowTimestamps = false;
                });
        });

        return await ResetCoreAsync(force, loggerFactory.CreateLogger<SystemProxyManager>(), CancellationToken.None);
    }

    private static async Task<int> ResetCoreAsync(bool force, ILogger<SystemProxyManager> logger, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsMacOS())
        {
            Console.WriteLine("Dev Proxy doesn't change the system proxy settings on this operating system. Nothing to reset.");
            return 0;
        }

        var liveOwner = await SystemProxyManager.FindLiveSystemProxyOwnerAsync(cancellationToken);
        if (liveOwner is not null && !force)
        {
            await Console.Error.WriteLineAsync($"Dev Proxy (PID: {liveOwner}) is running as the system proxy.");
            await Console.Error.WriteLineAsync("Use 'devproxy stop' to stop it, or 'devproxy system-proxy reset --force' to reset anyway.");
            return 1;
        }

        var result = new SystemProxyManager(logger).Restore();
        if (result is SystemProxyRestoreResult.Failed)
        {
            await Console.Error.WriteLineAsync("Couldn't restore the system proxy settings. To reset them manually, see https://go.devproxy.net?go=proxy-reset.");
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
            await StateManager.DeleteStateAsync(orphan.Pid, cancellationToken);
        }

        Console.WriteLine(result is SystemProxyRestoreResult.RestoredPrevious
            ? "Restored the system proxy settings from before Dev Proxy started."
            : "Turned off the system proxy.");
        return 0;
    }
}
