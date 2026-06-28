// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Text.RegularExpressions;
using DevProxy.Abstractions.Proxy;
using DevProxy.Proxy.Kestrel;
using DevProxy.Proxy.Kestrel.Internal;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DevProxy.Integration.Tests;

/// <summary>
/// Regression coverage for the daemon-readiness contract: when started with
/// <c>--port 0</c> the engine must publish the OS-assigned port back to the shared
/// <see cref="IProxyConfiguration"/>. The host persists that port in the daemon state
/// file, and the parent process's readiness poll, <c>devproxy stop</c>, and
/// <c>devproxy status</c> all require <c>Port &gt; 0</c>. Without the write-back,
/// detached mode (<c>--detach</c>) silently breaks: the daemon orphans, the parent
/// times out, and the system proxy is left on.
/// </summary>
public sealed class EnginePortPublishingTests
{
    [Fact]
    public async Task ExecuteAsync_WithPortZero_PublishesBoundPortToConfiguration()
    {
        var configuration = new TestProxyConfiguration
        {
            Port = 0,
            IPAddress = "127.0.0.1",
            AsSystemProxy = false,
        };
        var urlsToWatch = new HashSet<UrlToWatch>
        {
            new(new Regex("^https?://example.com/.*$", RegexOptions.Compiled | RegexOptions.IgnoreCase)),
        };

        var engine = new KestrelProxyEngine(
            CertificateAuthority.CreateDefault(),
            [],
            urlsToWatch,
            configuration,
            [],
            NullLoggerFactory.Instance);

        using var cts = new CancellationTokenSource();
        await engine.StartAsync(cts.Token);
        try
        {
            // The engine binds asynchronously inside ExecuteAsync, then publishes the
            // resolved port; give it a brief window to do so.
            var deadline = Environment.TickCount64 + 5_000;
            while (configuration.Port == 0 && Environment.TickCount64 < deadline)
            {
                await Task.Delay(25);
            }

            Assert.True(
                configuration.Port > 0,
                "Engine must publish the OS-assigned port back to IProxyConfiguration.Port when started with --port 0.");
        }
        finally
        {
            await cts.CancelAsync();
            try
            {
                await engine.StopAsync(CancellationToken.None);
            }
            catch (OperationCanceledException)
            {
                // Expected on shutdown.
            }

            engine.Dispose();
        }
    }
}
