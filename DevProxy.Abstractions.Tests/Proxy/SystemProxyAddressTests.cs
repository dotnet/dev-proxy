// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Abstractions.Proxy;
using Xunit;

namespace DevProxy.Abstractions.Tests.Proxy;

public class SystemProxyAddressTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("0.0.0.0")]
    [InlineData("::")]
    public void ResolveHost_WildcardOrEmpty_CollapsesToLoopback(string? ipAddress) =>
        Assert.Equal("127.0.0.1", SystemProxyAddress.ResolveHost(ipAddress));

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("10.0.0.5")]
    [InlineData("192.168.1.10")]
    public void ResolveHost_ExplicitAddress_PassesThrough(string ipAddress) =>
        Assert.Equal(ipAddress, SystemProxyAddress.ResolveHost(ipAddress));

    [Fact]
    public void ToHostPort_ComposesNormalizedHostAndPort() =>
        Assert.Equal("127.0.0.1:8000", SystemProxyAddress.ToHostPort("0.0.0.0", 8000));

    [Fact]
    public void ToHostPort_ExplicitAddress() =>
        Assert.Equal("10.0.0.5:9090", SystemProxyAddress.ToHostPort("10.0.0.5", 9090));

    [Fact]
    public void ToHostPort_NullAddress_UsesLoopback() =>
        Assert.Equal("127.0.0.1:8080", SystemProxyAddress.ToHostPort(null, 8080));
}
