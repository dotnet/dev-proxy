// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Plugins.ApiCenter;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DevProxy.Integration.Tests;

public sealed class ApiCenterClientTests
{
    [Theory]
    [InlineData("subscriptionId")]
    [InlineData("resourceGroupName")]
    [InlineData("serviceName")]
    [InlineData("workspaceName")]
    public void Constructor_MissingSetting_ErrorUsesConfigKeyName(string missingKey)
    {
        var configuration = new ApiCenterClientConfiguration
        {
            SubscriptionId = missingKey == "subscriptionId" ? "" : "sub",
            ResourceGroupName = missingKey == "resourceGroupName" ? "" : "rg",
            ServiceName = missingKey == "serviceName" ? "" : "svc",
            WorkspaceName = missingKey == "workspaceName" ? "" : "default"
        };

        var ex = Assert.Throws<ArgumentException>(() =>
            new ApiCenterClient(configuration, NullLogger<ApiCenterClient>.Instance));

        Assert.Equal($"Specify {missingKey} in the configuration.", ex.Message);
    }
}
