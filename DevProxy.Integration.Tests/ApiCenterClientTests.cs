// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Plugins.ApiCenter;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DevProxy.Integration.Tests;

public class ApiCenterClientTests
{
    private const string VariableName = "DEVPROXY_TEST_APICENTER_SUBSCRIPTION_ID";

    [Fact]
    public void Constructor_UnsetEnvironmentVariable_Throws()
    {
        Environment.SetEnvironmentVariable(VariableName, null);

        var ex = Assert.Throws<ArgumentException>(() => CreateClient($"@{VariableName}"));

        Assert.Contains(VariableName, ex.Message, StringComparison.Ordinal);
        Assert.Contains("subscriptionId", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Constructor_SetEnvironmentVariable_UsesItsValue()
    {
        Environment.SetEnvironmentVariable(VariableName, "00000000-0000-0000-0000-000000000000");
        try
        {
            var configuration = NewConfiguration($"@{VariableName}");
            using var client = new ApiCenterClient(configuration, NullLogger<ApiCenterClient>.Instance);

            Assert.Equal("00000000-0000-0000-0000-000000000000", configuration.SubscriptionId);
        }
        finally
        {
            Environment.SetEnvironmentVariable(VariableName, null);
        }
    }

    private static ApiCenterClient CreateClient(string subscriptionId) =>
        new(NewConfiguration(subscriptionId), NullLogger<ApiCenterClient>.Instance);

    private static ApiCenterClientConfiguration NewConfiguration(string subscriptionId) => new()
    {
        SubscriptionId = subscriptionId,
        ResourceGroupName = "rg",
        ServiceName = "svc"
    };
}
