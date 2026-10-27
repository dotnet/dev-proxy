// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Proxy;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace DevProxy.Tests;

[Collection("management-environment")]
public class HttpProxyEnvironmentTests
{
    [Theory]
    [InlineData("HTTP_PROXY")]
    [InlineData("HTTPS_PROXY")]
    [InlineData("ALL_PROXY")]
    [InlineData("NO_PROXY")]
    [InlineData("http_proxy")]
    [InlineData("https_proxy")]
    [InlineData("all_proxy")]
    [InlineData("no_proxy")]
    public void GetChangedVariables_ChangedOrRemovedOverride_ReportsNameUntilInitialValueRestored(string name)
    {
        var configuration = new ConfigurationBuilder().Build();
        using var environment = new ProcessEnvironment();
        environment.Apply(configuration, new Dictionary<string, string> { [name] = "initial" });
        var monitor = new HttpProxyEnvironment();
        Assert.Empty(monitor.GetChangedVariables());

        Environment.SetEnvironmentVariable(name, "changed");
        var expectedName = OperatingSystem.IsWindows() ? name.ToUpperInvariant() : name;
        Assert.Equal([expectedName], monitor.GetChangedVariables());
        Assert.Equal([expectedName], monitor.GetChangedVariables());

        Environment.SetEnvironmentVariable(name, null);
        Assert.Equal([expectedName], monitor.GetChangedVariables());

        Environment.SetEnvironmentVariable(name, "initial");
        Assert.Empty(monitor.GetChangedVariables());
    }

    [Fact]
    public void GetChangedVariables_ConfigChangesShadowedByCommandLine_DoesNotReportChange()
    {
        var monitor = new HttpProxyEnvironment();
        var cli = new Dictionary<string, string> { ["HTTPS_PROXY"] = "cli" };
        foreach (var value in new[] { "config", "changed" })
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["env:HTTPS_PROXY"] = value })
                .Build();
            using var environment = new ProcessEnvironment();
            environment.Apply(configuration, cli);
            Assert.Empty(monitor.GetChangedVariables());
        }
    }
}
