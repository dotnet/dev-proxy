// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Net;
using System.Text;
using Xunit;

namespace DevProxy.Parity.Tests;

/// <summary>
/// Parity rows that exercise the Kestrel engine over plain HTTP (absolute-form
/// proxy requests — no CONNECT, no TLS). These assert the engine relays the origin's
/// response faithfully: status, body, and request-body round-tripping.
/// </summary>
public sealed class PlainHttpParityTests
{
    [Fact]
    public async Task Get_NoBody_RelaysStatusAndBody()
    {
        await using var origin = await FakeOrigin.StartAsync();
        await using var proxy = await KestrelProxyHarness.StartAsync(origin.Host);
        using var client = proxy.CreateHttpClient();

        using var response = await client.GetAsync(
            new Uri($"http://{origin.Host}/get"));
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("hello get", body);
    }

    [Fact]
    public async Task Post_SmallBody_EchoesBodyAndHeader()
    {
        await using var origin = await FakeOrigin.StartAsync();
        await using var proxy = await KestrelProxyHarness.StartAsync(origin.Host);
        using var client = proxy.CreateHttpClient();

        using var content = new StringContent("ping", Encoding.UTF8);
        using var response = await client.PostAsync(
            new Uri($"http://{origin.Host}/echo"), content);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("ping", body);
        Assert.Equal("4", response.Headers.TryGetValues("X-Echo-Length", out var v)
            ? string.Join(string.Empty, v)
            : response.Content.Headers.TryGetValues("X-Echo-Length", out var cv)
                ? string.Join(string.Empty, cv)
                : null);
    }

    [Fact]
    public async Task Post_LargeBody_RoundTripsIntact()
    {
        await using var origin = await FakeOrigin.StartAsync();
        await using var proxy = await KestrelProxyHarness.StartAsync(origin.Host);
        using var client = proxy.CreateHttpClient();

        var payload = new string('x', 100_000);
        using var content = new StringContent(payload, Encoding.UTF8);
        using var response = await client.PostAsync(
            new Uri($"http://{origin.Host}/echo"), content);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(payload, body);
    }

    [Theory]
    [InlineData(404)]
    [InlineData(500)]
    [InlineData(418)]
    public async Task Get_StatusCode_RelaysStatus(int code)
    {
        await using var origin = await FakeOrigin.StartAsync();
        await using var proxy = await KestrelProxyHarness.StartAsync(origin.Host);
        using var client = proxy.CreateHttpClient();

        using var response = await client.GetAsync(
            new Uri($"http://{origin.Host}/status/{code}"));

        Assert.Equal(code, (int)response.StatusCode);
    }

    [Fact]
    public async Task Get_NoContent_RelaysEmptyBody()
    {
        await using var origin = await FakeOrigin.StartAsync();
        await using var proxy = await KestrelProxyHarness.StartAsync(origin.Host);
        using var client = proxy.CreateHttpClient();

        using var response = await client.GetAsync(
            new Uri($"http://{origin.Host}/nocontent"));
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(string.Empty, body);
    }

    [Fact]
    public async Task Get_LargeBody_RelaysIntact()
    {
        await using var origin = await FakeOrigin.StartAsync();
        await using var proxy = await KestrelProxyHarness.StartAsync(origin.Host);
        using var client = proxy.CreateHttpClient();

        using var response = await client.GetAsync(
            new Uri($"http://{origin.Host}/big/200000"));
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(200_000, body.Length);
        Assert.Equal(new string('A', 200_000), body);
    }

    [Fact]
    public async Task Get_RequestHeader_ReachesOrigin()
    {
        await using var origin = await FakeOrigin.StartAsync();
        await using var proxy = await KestrelProxyHarness.StartAsync(origin.Host);
        using var client = proxy.CreateHttpClient();

        using var request = new HttpRequestMessage(
            HttpMethod.Get, new Uri($"http://{origin.Host}/headers"));
        request.Headers.Add("X-Probe", "abc123");
        using var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal("probe=abc123", body);
    }
}
