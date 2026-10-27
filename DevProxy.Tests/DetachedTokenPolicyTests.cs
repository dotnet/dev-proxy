// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.State;
using System.Text.Json;
using Xunit;

namespace DevProxy.Tests;

public class DetachedTokenPolicyTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ShouldDisplayTokenFor_HonorsPersistedChildPolicy(bool suppressTokenOutput)
    {
        var state = new ProxyInstanceState { SuppressTokenOutput = suppressTokenOutput };
        var json = JsonSerializer.Serialize(state);
        var persisted = JsonSerializer.Deserialize<ProxyInstanceState>(json);

        Assert.NotNull(persisted);
        Assert.Equal(suppressTokenOutput, persisted.SuppressTokenOutput);
        Assert.Equal(ApiSecurity.ShouldDisplayToken && !suppressTokenOutput,
            ApiSecurity.ShouldDisplayTokenFor(persisted));
    }

    [Fact]
    public void ShouldDisplayTokenFor_LegacyState_PreservesParentPolicy()
    {
        var state = JsonSerializer.Deserialize<ProxyInstanceState>("{}");

        Assert.NotNull(state);
        Assert.Equal(ApiSecurity.ShouldDisplayToken, ApiSecurity.ShouldDisplayTokenFor(state));
    }
}
