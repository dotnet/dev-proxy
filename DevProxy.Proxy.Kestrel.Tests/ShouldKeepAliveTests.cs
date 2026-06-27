// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Proxy.Kestrel.Internal;

using Xunit;

namespace DevProxy.Proxy.Kestrel.Tests;

public class ShouldKeepAliveTests
{
    private static ParsedRequestHead Head(string version, params (string Name, string Value)[] headers) =>
        new("GET", "/", version, headers);

    [Fact]
    public void Http11_DefaultsToKeepAlive()
    {
        Assert.True(ProxyConnectionHandler.ShouldKeepAlive(Head("HTTP/1.1")));
    }

    [Fact]
    public void Http10_DefaultsToClose()
    {
        Assert.False(ProxyConnectionHandler.ShouldKeepAlive(Head("HTTP/1.0")));
    }

    [Fact]
    public void Http10_WithKeepAliveHeader_KeepsAlive()
    {
        Assert.True(ProxyConnectionHandler.ShouldKeepAlive(
            Head("HTTP/1.0", ("Connection", "keep-alive"))));
    }

    [Fact]
    public void Http11_WithConnectionClose_Closes()
    {
        Assert.False(ProxyConnectionHandler.ShouldKeepAlive(
            Head("HTTP/1.1", ("Connection", "close"))));
    }

    [Fact]
    public void ConnectionHeaderIsCaseInsensitive()
    {
        Assert.False(ProxyConnectionHandler.ShouldKeepAlive(
            Head("HTTP/1.1", ("connection", "Close"))));
    }

    [Fact]
    public void TransferEncoding_ForcesClose()
    {
        // We cannot reframe a chunked body yet, so refuse keep-alive to avoid
        // corrupting the next request on the connection.
        Assert.False(ProxyConnectionHandler.ShouldKeepAlive(
            Head("HTTP/1.1", ("Transfer-Encoding", "chunked"))));
    }

    [Fact]
    public void ExpectHeader_ForcesClose()
    {
        // 100-continue is unsupported; close rather than mishandle the body.
        Assert.False(ProxyConnectionHandler.ShouldKeepAlive(
            Head("HTTP/1.1", ("Expect", "100-continue"))));
    }
}
