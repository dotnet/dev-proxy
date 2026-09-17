// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Net;
using System.Text;
using DevProxy.Abstractions.Proxy.Http;
using DevProxy.Proxy.Kestrel.Http;

using Xunit;

namespace DevProxy.Proxy.Kestrel.Tests;

public class MutableHttpModelTests
{
    private static MutableHttpRequest Request(string method = "GET", string url = "https://example.com/", HeaderCollection? headers = null) =>
        new(method, new Uri(url), HttpVersion.Version11, headers ?? new HeaderCollection(), ReadOnlyMemory<byte>.Empty);

    [Fact]
    public void Request_UpperCasesMethod() =>
        Assert.Equal("POST", Request(method: "post").Method);

    [Fact]
    public void Request_Url_RoundTrips()
    {
        var request = Request();
        request.Url = "https://contoso.com/api";

        Assert.Equal("https://contoso.com/api", request.Url);
        Assert.Equal("contoso.com", request.RequestUri.Host);
    }

    [Fact]
    public void Request_IsWebSocketRequest_DetectsUpgradeHeader()
    {
        var headers = new HeaderCollection();
        headers.Add("Upgrade", "websocket");

        Assert.True(Request(headers: headers).IsWebSocketRequest);
        Assert.False(Request().IsWebSocketRequest);
    }

    [Fact]
    public void SetBody_UpdatesContentLengthHeader()
    {
        var response = new MutableHttpResponse(
            HttpStatusCode.OK, HttpVersion.Version11, new HeaderCollection(), ReadOnlyMemory<byte>.Empty);

        response.SetBody(Encoding.ASCII.GetBytes("abcd"));

        Assert.Equal("4", response.Headers.GetFirst("Content-Length")?.Value);
        Assert.True(response.HasBody);
    }

    [Fact]
    public void SetBodyString_SetsContentTypeWhenProvided()
    {
        var response = new MutableHttpResponse(
            HttpStatusCode.OK, HttpVersion.Version11, new HeaderCollection(), ReadOnlyMemory<byte>.Empty);

        response.SetBodyString("{}", "application/json");

        Assert.Equal("application/json", response.ContentType);
        Assert.Equal("{}", response.BodyString);
    }

    [Fact]
    public void ContentType_ReadsFromHeaders()
    {
        var headers = new HeaderCollection();
        headers.Add("Content-Type", "text/html; charset=utf-8");
        var response = new MutableHttpResponse(
            HttpStatusCode.OK, HttpVersion.Version11, headers, Encoding.UTF8.GetBytes("<html/>"));

        Assert.Equal("text/html; charset=utf-8", response.ContentType);
        Assert.Equal("<html/>", response.BodyString);
    }

    [Fact]
    public void Respond_SetsPluginMockedResponse()
    {
        var session = new CanonicalProxySession("abc", Request(), processId: null);

        session.Respond("nope", HttpStatusCode.TooManyRequests, []);

        Assert.True(session.RespondedByPlugin);
        Assert.True(session.HasResponse);
        Assert.Equal(HttpStatusCode.TooManyRequests, session.MutableResponse!.StatusCode);
        Assert.Equal("nope", session.MutableResponse.BodyString);
    }

    [Fact]
    public void SetOriginResponse_DoesNotFlagPluginMocked()
    {
        var session = new CanonicalProxySession("abc", Request(), processId: null);
        var origin = new MutableHttpResponse(
            HttpStatusCode.OK, HttpVersion.Version11, new HeaderCollection(), ReadOnlyMemory<byte>.Empty);

        session.SetOriginResponse(origin);

        Assert.True(session.HasResponse);
        Assert.False(session.RespondedByPlugin);
    }
}
