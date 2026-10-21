// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Net;
using System.Text.RegularExpressions;
using DevProxy.Abstractions.Proxy;
using DevProxy.Proxy.Kestrel;
using DevProxy.Proxy.Kestrel.Internal;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DevProxy.Integration.Tests;

/// <summary>
/// Regression coverage for <c>--timeout</c> / <c>timeoutSeconds</c>: Dev Proxy must
/// stop after the configured period without intercepted requests, and each request
/// must reset the countdown.
/// </summary>
public sealed class EngineInactivityTimeoutTests
{
    [Fact]
    public async Task ExecuteAsync_WithTimeout_StopsApplicationWhenIdle()
    {
        var configuration = CreateConfiguration(timeoutSeconds: 1);
        var lifetime = new FakeLifetime();

        await RunEngineAsync(configuration, lifetime, async () =>
        {
            var stopped = await Task.WhenAny(lifetime.StopRequested.Task, Task.Delay(10_000));
            Assert.Same(lifetime.StopRequested.Task, stopped);
        });
    }

    [Fact]
    public async Task ExecuteAsync_WithoutTimeout_DoesNotStopApplication()
    {
        var configuration = CreateConfiguration(timeoutSeconds: null);
        var lifetime = new FakeLifetime();

        await RunEngineAsync(configuration, lifetime, async () =>
        {
            await Task.Delay(1_500);
            Assert.False(lifetime.StopRequested.Task.IsCompleted);
        });
    }

    [Fact]
    public async Task ExecuteAsync_WithTimeout_RequestsResetCountdown()
    {
        var configuration = CreateConfiguration(timeoutSeconds: 2);
        var lifetime = new FakeLifetime();

        await RunEngineAsync(configuration, lifetime, async () =>
        {
            using var handler = new HttpClientHandler
            {
                Proxy = new WebProxy($"http://127.0.0.1:{configuration.Port}"),
                UseProxy = true,
            };
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };

            // Keep sending requests for longer than the timeout; each one must reset it.
            var until = Environment.TickCount64 + 3_500;
            while (Environment.TickCount64 < until)
            {
                try
                {
                    using var _ = await client.GetAsync(new Uri("http://127.0.0.1:9/"));
                }
                catch (HttpRequestException)
                {
                    // The origin is unreachable; only the proxy seeing the request matters.
                }
                Assert.False(lifetime.StopRequested.Task.IsCompleted, "Dev Proxy stopped despite ongoing requests.");
                await Task.Delay(400);
            }

            var stopped = await Task.WhenAny(lifetime.StopRequested.Task, Task.Delay(10_000));
            Assert.Same(lifetime.StopRequested.Task, stopped);
        });
    }

    private static TestProxyConfiguration CreateConfiguration(long? timeoutSeconds) => new()
    {
        Port = 0,
        IPAddress = "127.0.0.1",
        AsSystemProxy = false,
        TimeoutSeconds = timeoutSeconds,
    };

    private static async Task RunEngineAsync(
        TestProxyConfiguration configuration, IHostApplicationLifetime lifetime, Func<Task> assert)
    {
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
            NullLoggerFactory.Instance,
            hostApplicationLifetime: lifetime);

        using var cts = new CancellationTokenSource();
        await engine.StartAsync(cts.Token);
        try
        {
            var deadline = Environment.TickCount64 + 5_000;
            while (configuration.Port == 0 && Environment.TickCount64 < deadline)
            {
                await Task.Delay(25);
            }
            Assert.True(configuration.Port > 0, "Engine did not bind a port.");

            await assert();
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

    private sealed class FakeLifetime : IHostApplicationLifetime
    {
        public TaskCompletionSource StopRequested { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() => StopRequested.TrySetResult();
    }
}
