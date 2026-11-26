// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Globalization;
using System.Net;
using DevProxy.Abstractions.Plugins;
using DevProxy.Abstractions.Proxy;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DevProxy.Integration.Tests;

/// <summary>
/// Every watched request must end with <see cref="MessageType.FinishedProcessingRequest"/>,
/// also when it fails or is cancelled: the console formatter buffers a request's lines
/// until then, so a missing terminator leaks the buffer and never prints the lines.
/// </summary>
public sealed class RequestLogCompletionIntegrationTests
{
    [Fact]
    public async Task UnreachableUpstream_FinishesRequestLog()
    {
        var host = $"127.0.0.1:{NetUtil.GetFreePort().ToString(CultureInfo.InvariantCulture)}";
        var factory = new CapturingLoggerFactory();
        await using var proxy = await KestrelProxyHarness.StartAsync(host, loggerFactory: factory);
        using var client = proxy.CreateHttpClient();

        using var response = await client.GetAsync(new Uri($"http://{host}/unreachable"));

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        _ = Assert.Single(factory.LogsOfType(MessageType.InterceptedRequest));
        _ = Assert.Single(factory.LogsOfType(MessageType.FinishedProcessingRequest));
    }

    [Fact]
    public async Task ClientCancelsDuringRequestPhase_FinishesRequestLog()
    {
        await using var origin = await FakeOrigin.StartAsync();
        var urlsToWatch = KestrelProxyHarness.BuildUrlsToWatch(origin.Host);
        var blocking = new BlockingPlugin(urlsToWatch, blockBeforeRequest: true);
        IPlugin[] plugins = [blocking, new BlockingPlugin(urlsToWatch)];

        await AssertFinishedAfterClientCancelsAsync(origin.Host, plugins, blocking);
    }

    [Fact]
    public async Task ClientCancelsDuringResponsePhase_FinishesRequestLog()
    {
        await using var origin = await FakeOrigin.StartAsync();
        var urlsToWatch = KestrelProxyHarness.BuildUrlsToWatch(origin.Host);
        var blocking = new BlockingPlugin(urlsToWatch, blockBeforeResponse: true);
        IPlugin[] plugins = [new MockShortCircuitPlugin(urlsToWatch), blocking, new BlockingPlugin(urlsToWatch)];

        await AssertFinishedAfterClientCancelsAsync(origin.Host, plugins, blocking);
    }

    private static async Task AssertFinishedAfterClientCancelsAsync(string host, IPlugin[] plugins, BlockingPlugin blocking)
    {
        var factory = new CapturingLoggerFactory();
        await using var proxy = await KestrelProxyHarness.StartAsync(host, plugins, factory);
        using var client = proxy.CreateHttpClient();
        using var cts = new CancellationTokenSource();

        var request = client.GetAsync(new Uri($"http://{host}/get"), cts.Token);
        await blocking.Entered.WaitAsync(TimeSpan.FromSeconds(10));
        await cts.CancelAsync();
        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (factory.LogsOfType(MessageType.FinishedProcessingRequest).Count == 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(25);
        }

        _ = Assert.Single(factory.LogsOfType(MessageType.FinishedProcessingRequest));
    }

    /// <summary>
    /// Blocks in the chosen phase until the connection is cancelled. Exceptions from a
    /// plugin are swallowed by the pipeline, so the cancellation surfaces at the
    /// pipeline's own check before the next plugin runs.
    /// </summary>
    private sealed class BlockingPlugin(
        ISet<UrlToWatch> urlsToWatch, bool blockBeforeRequest = false, bool blockBeforeResponse = false)
        : BasePlugin(NullLogger.Instance, urlsToWatch)
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Entered => _entered.Task;

        public override string Name => nameof(BlockingPlugin);

        public override Task BeforeRequestAsync(ProxyRequestArgs e, CancellationToken cancellationToken) =>
            blockBeforeRequest ? BlockAsync(cancellationToken) : Task.CompletedTask;

        public override Task BeforeResponseAsync(ProxyResponseArgs e, CancellationToken cancellationToken) =>
            blockBeforeResponse ? BlockAsync(cancellationToken) : Task.CompletedTask;

        private Task BlockAsync(CancellationToken cancellationToken)
        {
            _ = _entered.TrySetResult();
            return Task.Delay(Timeout.Infinite, cancellationToken);
        }
    }
}
