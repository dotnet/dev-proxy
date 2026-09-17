// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Abstractions.Proxy.Http;
using Xunit;

namespace DevProxy.Abstractions.Tests.Proxy.Http;

public class ForwardingInvariantsTests
{
    [Theory]
    [InlineData("Connection")]
    [InlineData("proxy-connection")]
    [InlineData("KEEP-ALIVE")]
    [InlineData("Transfer-Encoding")]
    [InlineData("te")]
    [InlineData("Trailer")]
    [InlineData("Upgrade")]
    [InlineData("Proxy-Authenticate")]
    [InlineData("Proxy-Authorization")]
    public void HopByHopHeaders_ContainsExpected_CaseInsensitive(string name) =>
        Assert.Contains(name, (IReadOnlySet<string>)ForwardingInvariants.HopByHopHeaders);

    [Theory]
    [InlineData("Content-Type")]
    [InlineData("Host")]
    [InlineData("Set-Cookie")]
    [InlineData("Authorization")]
    public void HopByHopHeaders_ExcludesEndToEndHeaders(string name) =>
        Assert.DoesNotContain(name, (IReadOnlySet<string>)ForwardingInvariants.HopByHopHeaders);
}
