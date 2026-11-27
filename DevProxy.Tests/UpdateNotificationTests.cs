// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Abstractions.Proxy;
using DevProxy.Commands;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Net;
using System.Text;
using Xunit;

namespace DevProxy.Tests;

public class UpdateNotificationTests
{
    [Fact]
    public async Task CheckForNewVersionAsync_Canceled_ReturnsNullWithoutThrowing()
    {
        using var handler = new HangingHandler();
        using var httpClient = new HttpClient(handler);
        var updateNotification = new UpdateNotification(httpClient);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        var result = await updateNotification.CheckForNewVersionAsync(ReleaseType.Stable, cts.Token)
            .WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Null(result);
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task CheckForNewVersionInBackgroundAsync_RequestHangs_DoesNotBlockCallerAndCompletesOnCancel()
    {
        using var handler = new HangingHandler();
        using var httpClient = new HttpClient(handler);
        var updateNotification = new UpdateNotification(httpClient);
        var logger = new CountingLogger();
        using var cts = new CancellationTokenSource();

        var check = DevProxyCommand.CheckForNewVersionInBackgroundAsync(updateNotification, ReleaseType.Stable, logger, cts.Token);
        await handler.RequestStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.False(check.IsCompleted);

        await cts.CancelAsync();
        await check.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(check.IsCompletedSuccessfully);
        Assert.Equal(0, logger.Count);
    }

    [Fact]
    public async Task CheckForNewVersionInBackgroundAsync_NewVersionAvailable_LogsNotification()
    {
        using var handler = new StaticHandler("""[{"name":"v999.0.0","html_url":"https://example.com"}]""");
        using var httpClient = new HttpClient(handler);
        var updateNotification = new UpdateNotification(httpClient);
        var logger = new CountingLogger();

        await DevProxyCommand.CheckForNewVersionInBackgroundAsync(updateNotification, ReleaseType.Stable, logger, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(1, logger.Count);
    }

    private sealed class HangingHandler : HttpMessageHandler
    {
        private int _requestCount;

        public int RequestCount => _requestCount;
        public TaskCompletionSource RequestStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            _ = Interlocked.Increment(ref _requestCount);
            _ = RequestStarted.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new InvalidOperationException("Unreachable");
        }
    }

    private sealed class StaticHandler(string content) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(content, Encoding.UTF8, "application/json")
            });
    }

    private sealed class CountingLogger : ILogger
    {
        private int _count;

        public int Count => _count;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => NullLogger.Instance.BeginScope(state);
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Interlocked.Increment(ref _count);
    }
}
