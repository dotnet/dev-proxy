// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Abstractions.Proxy;

namespace DevProxy.Proxy;

/// <summary>
/// Keeps the system proxy registered across in-process hot-reload restarts.
///
/// <para>
/// Created once per process, outside the restart loop. Each host run wraps its
/// <see cref="ISystemProxyManager"/> with <see cref="Track"/>, so:
/// </para>
/// <list type="bullet">
///   <item>stopping the host for a restart keeps the system proxy registered,</item>
///   <item>the next run skips re-registering when the endpoint is unchanged and applies
///         the new endpoint (without turning the proxy off first) when it changed,</item>
///   <item>a restarted run that no longer uses the system proxy releases it once it
///         has started (<see cref="ReleaseUnclaimed"/>),</item>
///   <item>the process releases anything still registered after the restart loop ends
///         (<see cref="Release"/>), for example when a restart fails.</item>
/// </list>
/// </summary>
internal sealed class SystemProxySession(Func<bool> isRestarting)
{
    private readonly Lock _lock = new();
    private ISystemProxyManager? _currentManager;
    private string? _appliedEndpoint;
    private bool _claimedInCurrentRun;

    public SystemProxySession() : this(() => ConfigFileWatcher.IsRestarting)
    {
    }

    /// <summary>
    /// The endpoint (host:port) the system proxy currently points to, or null.
    /// </summary>
    public string? AppliedEndpoint
    {
        get
        {
            lock (_lock)
            {
                return _appliedEndpoint;
            }
        }
    }

    /// <summary>
    /// Marks the start of a host run. Call before each run of the restart loop.
    /// </summary>
    public void BeginRun()
    {
        lock (_lock)
        {
            _claimedInCurrentRun = false;
        }
    }

    /// <summary>
    /// Wraps the manager of the current host run so its calls go through this session.
    /// </summary>
    public ISystemProxyManager Track(ISystemProxyManager manager)
    {
        ArgumentNullException.ThrowIfNull(manager);

        lock (_lock)
        {
            _currentManager = manager;
        }
        return new TrackedSystemProxyManager(this, manager);
    }

    /// <summary>
    /// Releases a system proxy registration kept from a previous run when the current
    /// run started without registering the system proxy (for example, after a reload set
    /// <c>asSystemProxy</c> to false). Call once the proxy engine has started.
    /// </summary>
    public void ReleaseUnclaimed()
    {
        lock (_lock)
        {
            if (_appliedEndpoint is null || _claimedInCurrentRun || _currentManager is null)
            {
                return;
            }

            _currentManager.Disable();
            _appliedEndpoint = null;
        }
    }

    /// <summary>
    /// Releases the system proxy if it's still registered. Call after the restart loop
    /// ends, when the host that registered it has already been disposed.
    /// </summary>
    public void Release(ISystemProxyManager manager)
    {
        ArgumentNullException.ThrowIfNull(manager);

        lock (_lock)
        {
            if (_appliedEndpoint is null)
            {
                return;
            }

            manager.Disable();
            _appliedEndpoint = null;
        }
    }

    private void Enable(ISystemProxyManager manager, string? ipAddress, int port)
    {
        var endpoint = SystemProxyAddress.ToHostPort(ipAddress, port);
        lock (_lock)
        {
            _claimedInCurrentRun = true;
            if (string.Equals(_appliedEndpoint, endpoint, StringComparison.Ordinal))
            {
                return;
            }

            manager.Enable(ipAddress, port);
            _appliedEndpoint = endpoint;
        }
    }

    private void Disable(ISystemProxyManager manager)
    {
        lock (_lock)
        {
            if (_appliedEndpoint is not null && isRestarting())
            {
                return;
            }

            manager.Disable();
            _appliedEndpoint = null;
        }
    }

    private sealed class TrackedSystemProxyManager(SystemProxySession session, ISystemProxyManager manager) : ISystemProxyManager
    {
        public void Enable(string? ipAddress, int port) => session.Enable(manager, ipAddress, port);

        public void Disable() => session.Disable(manager);
    }
}
