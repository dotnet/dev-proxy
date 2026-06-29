// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Proxy.Kestrel.Internal;
using Xunit;

namespace DevProxy.Proxy.Kestrel.Tests;

public class ConnectAuthorityParserTests
{
    // ── reg-name / IPv4 hosts ───────────────────────────────────────────
    [Fact]
    public void Parse_HostWithPort()
    {
        Assert.True(ConnectAuthorityParser.TryParse("example.com:8443", 443, out var a));
        Assert.Equal("example.com", a.Host);
        Assert.Equal("example.com", a.UrlHost);
        Assert.Equal(8443, a.Port);
        Assert.False(a.IsIPv6);
    }

    [Fact]
    public void Parse_HostWithoutPort_UsesDefault()
    {
        Assert.True(ConnectAuthorityParser.TryParse("example.com", 443, out var a));
        Assert.Equal("example.com", a.Host);
        Assert.Equal(443, a.Port);
    }

    [Fact]
    public void Parse_IPv4WithPort()
    {
        Assert.True(ConnectAuthorityParser.TryParse("10.0.0.1:9000", 443, out var a));
        Assert.Equal("10.0.0.1", a.Host);
        Assert.Equal(9000, a.Port);
        Assert.False(a.IsIPv6);
    }

    [Fact]
    public void Parse_PunycodeHost()
    {
        Assert.True(ConnectAuthorityParser.TryParse("xn--n3h.com:443", 443, out var a));
        Assert.Equal("xn--n3h.com", a.Host);
        Assert.Equal(443, a.Port);
    }

    // ── IPv6 literals ───────────────────────────────────────────────────
    [Fact]
    public void Parse_IPv6WithPort()
    {
        Assert.True(ConnectAuthorityParser.TryParse("[2001:db8::1]:8443", 443, out var a));
        Assert.Equal("2001:db8::1", a.Host);
        Assert.Equal("[2001:db8::1]", a.UrlHost);
        Assert.Equal(8443, a.Port);
        Assert.True(a.IsIPv6);
    }

    [Fact]
    public void Parse_IPv6WithoutPort_UsesDefault()
    {
        Assert.True(ConnectAuthorityParser.TryParse("[::1]", 443, out var a));
        Assert.Equal("::1", a.Host);
        Assert.Equal("[::1]", a.UrlHost);
        Assert.Equal(443, a.Port);
        Assert.True(a.IsIPv6);
    }

    // ── malformed → rejected ────────────────────────────────────────────
    [Theory]
    [InlineData("")]                       // empty
    [InlineData("   ")]                     // whitespace
    [InlineData(":443")]                    // empty host
    [InlineData("example.com:")]            // empty port
    [InlineData("example.com:0")]           // port out of range (low)
    [InlineData("example.com:70000")]       // port out of range (high)
    [InlineData("example.com:-1")]          // negative port
    [InlineData("example.com:abc")]         // non-numeric port
    [InlineData("::1")]                      // bare IPv6 (must be bracketed)
    [InlineData("2001:db8::1:443")]          // bare IPv6 + port, ambiguous
    [InlineData("[::1")]                     // unterminated bracket
    [InlineData("[::1]extra")]               // junk after ]
    [InlineData("[notipv6]:443")]            // bracketed but not an IPv6 literal
    [InlineData("[1.2.3.4]:443")]            // IPv4 in brackets is not IPv6
    [InlineData("bad host:443")]             // invalid host name (space)
    public void Parse_Malformed_ReturnsFalse(string authority)
    {
        Assert.False(ConnectAuthorityParser.TryParse(authority, 443, out _));
    }
}
