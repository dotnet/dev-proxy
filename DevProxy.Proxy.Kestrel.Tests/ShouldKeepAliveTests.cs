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
    public void TransferEncoding_KeepsAlive_NowThatChunkedIsReframed()
    {
        // The chunked body is decoded and re-framed with Content-Length before this
        // runs, so a chunked request no longer forces the connection closed.
        Assert.True(ProxyConnectionHandler.ShouldKeepAlive(
            Head("HTTP/1.1", ("Transfer-Encoding", "chunked"))));
    }

    [Fact]
    public void ExpectHeader_KeepsAlive_NowThat100ContinueIsHandled()
    {
        // The proxy answers Expect: 100-continue and reads the body, so the connection
        // stays framable and may be kept alive.
        Assert.True(ProxyConnectionHandler.ShouldKeepAlive(
            Head("HTTP/1.1", ("Expect", "100-continue"))));
    }

    [Fact]
    public void ConnectionClose_StillWins_OverChunked()
    {
        Assert.False(ProxyConnectionHandler.ShouldKeepAlive(
            Head("HTTP/1.1", ("Transfer-Encoding", "chunked"), ("Connection", "close"))));
    }
}
