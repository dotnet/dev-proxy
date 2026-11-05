// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

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

    private async Task<int> ResetAsync(ParseResult parseResult, CancellationToken cancellationToken)
    {
        var force = parseResult.GetValue(_forceOption);

        var liveOwner = await SystemProxyManager.FindLiveSystemProxyOwnerAsync(cancellationToken);
        if (liveOwner is not null && !force)
        {
            await Console.Error.WriteLineAsync($"Dev Proxy (PID: {liveOwner}) is running as the system proxy.");
            await Console.Error.WriteLineAsync("Use 'devproxy stop' to stop it, or 'devproxy system-proxy reset --force' to reset anyway.");
            return 1;
        }

        var result = new SystemProxyManager(_systemProxyLogger).Restore();
        if (result is SystemProxyRestoreResult.Failed)
        {
            await Console.Error.WriteLineAsync("Couldn't restore the system proxy settings. To reset them manually, see https://go.devproxy.net?go=proxy-reset.");
            return 1;
        }

        // The settings are restored, so records of dead instances that registered as the
        // system proxy are stale. Remove them so they don't trigger another restore later.
        foreach (var orphan in await StateManager.GetOrphanedSystemProxyStatesAsync(cancellationToken))
        {
            await StateManager.DeleteStateAsync(orphan.Pid, cancellationToken);
        }

        switch (result)
        {
            case SystemProxyRestoreResult.RestoredPrevious:
                Console.WriteLine("Restored the system proxy settings from before Dev Proxy started.");
                return 0;
            case SystemProxyRestoreResult.Disabled:
                Console.WriteLine("Turned off the system proxy.");
                return 0;
            case SystemProxyRestoreResult.NotSupported:
            default:
                Console.WriteLine("Dev Proxy doesn't change the system proxy settings on this operating system. Nothing to reset.");
                return 0;
        }
    }
}
