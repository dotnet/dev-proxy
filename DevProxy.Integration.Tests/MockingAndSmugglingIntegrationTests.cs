// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using DevProxy.Abstractions.Plugins;
using DevProxy.Abstractions.Proxy;
using Xunit;

namespace DevProxy.Integration.Tests;

/// <summary>
/// Integration scenarios for the mocking short-circuit and the request-smuggling guard.
/// </summary>
public sealed class MockingAndSmugglingIntegrationTests
{
    [Fact]
    public async Task PluginRespond_ShortCircuits_OriginNeverContacted()
    {
        // Watch a host the origin does NOT serve — if the mock did not short-circuit,
        // the forward would fail, proving the response came purely from the plugin.
        const string watchedHost = "127.0.0.1:59999";
        var urlsToWatch = new HashSet<UrlToWatch>
        {
            new(new System.Text.RegularExpressions.Regex(
                "^https?://127\\.0\\.0\\.1:59999/.*$",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase)),
        };
        var plugins = new IPlugin[] { new MockShortCircuitPlugin(urlsToWatch) };

        await using var proxy = await KestrelProxyHarness.StartAsync(watchedHost, plugins);
        using var client = proxy.CreateHttpClient();

        using var response = await client.GetAsync(new Uri($"http://{watchedHost}/anything"));
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(MockShortCircuitPlugin.MockStatus, (int)response.StatusCode);
        Assert.Equal(MockShortCircuitPlugin.MockBody, body);
        Assert.Contains(
            response.Headers.TryGetValues("X-Mocked", out var v) ? v : [],
            h => string.Equals(h, "true", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ContentLengthPlusChunked_IsRejectedWith400()
    {
        await using var origin = await FakeOrigin.StartAsync();
        await using var proxy = await KestrelProxyHarness.StartAsync(origin.Host);

        // Raw absolute-form proxy request carrying BOTH Content-Length and
        // Transfer-Encoding: chunked — a classic request-smuggling vector
        // (RFC 9112 §6.3.3) the engine must refuse.
        var raw =
            $"GET http://{origin.Host}/get HTTP/1.1\r\n" +
            $"Host: {origin.Host}\r\n" +
            "Content-Length: 5\r\n" +
            "Transfer-Encoding: chunked\r\n" +
            "\r\n" +
            "0\r\n\r\n";

        using var tcp = new TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, proxy.Port);
        var stream = tcp.GetStream();
        var bytes = Encoding.ASCII.GetBytes(raw);
        await stream.WriteAsync(bytes);
        await stream.FlushAsync();

        var response = await ReadStatusLineAsync(stream);

        Assert.Contains("400", response, StringComparison.Ordinal);
    }

    private static async Task<string> ReadStatusLineAsync(NetworkStream stream)
    {
        var buffer = new byte[1024];
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var read = await stream.ReadAsync(buffer, cts.Token);
        return Encoding.ASCII.GetString(buffer, 0, read);
    }
}
