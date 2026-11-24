// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Proxy;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DevProxy.Tests;

/// <summary>
/// Verifies that shutting down the proxy (Ctrl+C, <c>devproxy stop</c>) flushes an
/// active recording so recording plugins still run (dotnet/dev-proxy#1945).
/// </summary>
public sealed class InteractiveConsoleServiceTests
{
    private static (InteractiveConsoleService service, FakeProxyStateController controller) CreateService()
    {
        var controller = new FakeProxyStateController();
        var service = new InteractiveConsoleService(
            controller,
            new FakeProxyConfiguration(),
            new RecordingConsole { IsInputRedirected = true },
            new FakeServer(),
            new FakeLifetime(),
            NullLogger<InteractiveConsoleService>.Instance);
        return (service, controller);
    }

    [Fact]
    public async Task StopAsync_WhileRecording_StopsRecording()
    {
        var (service, controller) = CreateService();
        controller.ProxyState.IsRecording = true;

        await service.StopAsync(CancellationToken.None);

        Assert.Equal(1, controller.StopRecordingCalls);
    }

    [Fact]
    public async Task StopAsync_NotRecording_DoesNotStopRecording()
    {
        var (service, controller) = CreateService();

        await service.StopAsync(CancellationToken.None);

        Assert.Equal(0, controller.StopRecordingCalls);
    }

    private sealed class FakeServer : IServer
    {
        public IFeatureCollection Features { get; } = new FeatureCollection();
        public void Dispose() { }
        public Task StartAsync<TContext>(IHttpApplication<TContext> application, CancellationToken cancellationToken) where TContext : notnull => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class FakeLifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() { }
    }
}
