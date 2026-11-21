// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Proxy;

using Xunit;

namespace DevProxy.Tests;

public class SystemProxyManagerTests
{
    [Fact]
    public void CreateToggleScriptStartInfo_PreservesPathAndArgumentsContainingSpaces()
    {
        var startInfo = SystemProxyManager.CreateToggleScriptStartInfo(
            "/Applications/Dev Proxy/toggle-proxy.sh",
            ["on", "proxy host", "8897"]);

        Assert.Equal("/bin/bash", startInfo.FileName);
        Assert.Empty(startInfo.Arguments);
        Assert.Equal(
            ["/Applications/Dev Proxy/toggle-proxy.sh", "on", "proxy host", "8897"],
            startInfo.ArgumentList);
    }

    [Fact]
    public void SystemProxySession_RestartWithSameEndpoint_KeepsSystemProxyRegistered()
    {
        var restarting = false;
        var manager = new CountingSystemProxyManager();
        var session = new SystemProxySession(() => restarting);

        session.BeginRun();
        var run = session.Track(manager);
        run.Enable("0.0.0.0", 8000);
        restarting = true;
        run.Disable();

        restarting = false;
        session.BeginRun();
        run = session.Track(manager);
        run.Enable("127.0.0.1", 8000);
        session.ReleaseUnclaimed();

        Assert.Equal(1, manager.EnableCount);
        Assert.Equal(0, manager.DisableCount);

        run.Disable();

        Assert.Equal(1, manager.DisableCount);
        Assert.Null(session.AppliedEndpoint);
    }

    [Fact]
    public void SystemProxySession_RestartWithNewPort_AppliesNewEndpointWithoutDisabling()
    {
        var restarting = false;
        var manager = new CountingSystemProxyManager();
        var session = new SystemProxySession(() => restarting);

        session.BeginRun();
        session.Track(manager).Enable("127.0.0.1", 8000);
        restarting = true;
        session.Track(manager).Disable();

        restarting = false;
        session.BeginRun();
        session.Track(manager).Enable("127.0.0.1", 8001);

        Assert.Equal(2, manager.EnableCount);
        Assert.Equal(0, manager.DisableCount);
        Assert.Equal("127.0.0.1:8001", session.AppliedEndpoint);
    }

    [Fact]
    public void SystemProxySession_RestartWithoutSystemProxy_ReleasesPreviousRegistration()
    {
        var restarting = false;
        var manager = new CountingSystemProxyManager();
        var session = new SystemProxySession(() => restarting);

        session.BeginRun();
        session.Track(manager).Enable("127.0.0.1", 8000);
        restarting = true;
        session.Track(manager).Disable();

        restarting = false;
        session.BeginRun();
        _ = session.Track(manager);
        session.ReleaseUnclaimed();

        Assert.Equal(1, manager.DisableCount);
        Assert.Null(session.AppliedEndpoint);
    }

    [Fact]
    public void SystemProxySession_FailedRestart_ReleaseDisablesSystemProxy()
    {
        var manager = new CountingSystemProxyManager();
        var session = new SystemProxySession(() => true);

        session.BeginRun();
        session.Track(manager).Enable("127.0.0.1", 8000);
        session.Track(manager).Disable();
        Assert.Equal(0, manager.DisableCount);

        var cleanup = new CountingSystemProxyManager();
        session.Release(cleanup);
        session.Release(cleanup);

        Assert.Equal(1, cleanup.DisableCount);
        Assert.Null(session.AppliedEndpoint);
    }

    [Fact]
    public void SystemProxySession_FailedEnable_RetriesOnRestartAndStillCleansUp()
    {
        var restarting = false;
        var manager = new CountingSystemProxyManager { EnableSucceeds = false };
        var session = new SystemProxySession(() => restarting);

        session.BeginRun();
        session.Track(manager).Enable("127.0.0.1", 8000);
        restarting = true;
        session.Track(manager).Disable();

        restarting = false;
        manager.EnableSucceeds = true;
        session.BeginRun();
        session.Track(manager).Enable("127.0.0.1", 8000);

        Assert.Equal(2, manager.EnableCount);
        Assert.Equal(0, manager.DisableCount);
        Assert.Equal("127.0.0.1:8000", session.AppliedEndpoint);

        manager.EnableSucceeds = false;
        restarting = true;
        session.Track(manager).Disable();
        session.BeginRun();
        session.Track(manager).Enable("127.0.0.1", 8001);
        Assert.Null(session.AppliedEndpoint);

        var cleanup = new CountingSystemProxyManager();
        session.Release(cleanup);

        Assert.Equal(1, cleanup.DisableCount);
    }

    private sealed class CountingSystemProxyManager : IConfirmingSystemProxyManager
    {
        public bool EnableSucceeds { get; set; } = true;
        public int EnableCount { get; private set; }
        public int DisableCount { get; private set; }

        public void Enable(string? ipAddress, int port) => _ = TryEnable(ipAddress, port);

        public bool TryEnable(string? ipAddress, int port)
        {
            EnableCount++;
            return EnableSucceeds;
        }

        public void Disable() => DisableCount++;
    }
}
