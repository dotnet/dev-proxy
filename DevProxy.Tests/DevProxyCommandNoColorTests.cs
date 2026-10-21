// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Commands;

using Xunit;

namespace DevProxy.Tests;

public class DevProxyCommandNoColorTests
{
    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("1", true)]
    [InlineData("true", true)]
    public void ShouldDisableColor_RespectsNoColorEnv(string? noColor, bool expected)
    {
        Assert.Equal(expected, DevProxyCommand.ShouldDisableColor([], noColor, null));
    }

    [Fact]
    public void ShouldDisableColor_NoColorOption_DisablesColor()
    {
        Assert.True(DevProxyCommand.ShouldDisableColor(["devproxy", "--no-color"], "", null));
    }

    [Theory]
    [InlineData("dumb", true)]
    [InlineData("DUMB", true)]
    [InlineData("xterm-256color", false)]
    public void ShouldDisableColor_RespectsTerm(string term, bool expected)
    {
        Assert.Equal(expected, DevProxyCommand.ShouldDisableColor([], null, term));
    }
}
