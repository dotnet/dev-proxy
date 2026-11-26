// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Commands;

using Xunit;

namespace DevProxy.Tests;

public class DevProxyConfigOptionsTests
{
    [Theory]
    [InlineData(new string[] { }, false)]
    [InlineData(new[] { "--discover" }, true)]
    [InlineData(new[] { "--discover", "true" }, true)]
    [InlineData(new[] { "--discover", "false" }, false)]
    [InlineData(new[] { "--discover", "false", "--port", "0" }, false)]
    public void Discover_ParsesOptionalValue(string[] args, bool expected)
    {
        var options = new DevProxyConfigOptions();
        options.ParseOptions(args);

        Assert.Equal(expected, options.Discover);
    }
}
