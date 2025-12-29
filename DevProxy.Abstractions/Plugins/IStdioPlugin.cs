// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Abstractions.Proxy;

namespace DevProxy.Abstractions.Plugins;

/// <summary>
/// Interface for plugins that can intercept stdio messages.
/// Plugins that implement both IPlugin and IStdioPlugin can participate
/// in both HTTP proxy and stdio proxy scenarios.
/// </summary>
public interface IStdioPlugin
{
    /// <summary>
    /// Gets the name of the plugin.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Gets whether the plugin is enabled.
    /// </summary>
    bool Enabled { get; }

    /// <summary>
    /// Called before stdin message is forwarded to the child process.
    /// Plugins can inspect, modify, or consume the message.
    /// Set ResponseState.HasBeenSet to true to prevent the message from being forwarded.
    /// </summary>
    /// <param name="e">The event arguments containing the stdin message.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task BeforeStdinAsync(StdioRequestArgs e, CancellationToken cancellationToken);

    /// <summary>
    /// Called after stdout message is received from the child process.
    /// Plugins can inspect, modify, or consume the message.
    /// Set ResponseState.HasBeenSet to true to prevent the message from being forwarded.
    /// </summary>
    /// <param name="e">The event arguments containing the stdout message.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task AfterStdoutAsync(StdioResponseArgs e, CancellationToken cancellationToken);

    /// <summary>
    /// Called after stderr message is received from the child process.
    /// Plugins can inspect, modify, or consume the message.
    /// Set ResponseState.HasBeenSet to true to prevent the message from being forwarded.
    /// </summary>
    /// <param name="e">The event arguments containing the stderr message.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task AfterStderrAsync(StdioResponseArgs e, CancellationToken cancellationToken);

    /// <summary>
    /// Called after a stdin/stdout pair has been logged.
    /// Useful for recording and reporting plugins.
    /// </summary>
    /// <param name="e">The event arguments containing the request log.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task AfterStdioRequestLogAsync(StdioRequestLogArgs e, CancellationToken cancellationToken);

    /// <summary>
    /// Called after recording has stopped.
    /// Useful for reporting plugins that need to process all recorded messages.
    /// </summary>
    /// <param name="e">The event arguments containing all recorded logs.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task AfterStdioRecordingStopAsync(StdioRecordingArgs e, CancellationToken cancellationToken);
}
