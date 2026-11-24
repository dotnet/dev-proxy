// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Commands;
using DevProxy.Proxy;
using Microsoft.Extensions.Configuration;

using Xunit;

namespace DevProxy.Tests;

[Collection("management-environment")]
public class DevProxyCommandNoColorTests
{
    [Theory]
    [InlineData("NO_COLOR", "1", "", false)]
    [InlineData("NO_COLOR", "1", "", true)]
    [InlineData("TERM", "dumb", "xterm-256color", false)]
    [InlineData("TERM", "dumb", "xterm-256color", true)]
    public void NoColor_AddedChangedAndRemovedEnvironmentOverride_UsesCurrentValues(
        string name, string disablingValue, string enablingValue, bool inheritedDisablesColor)
    {
        var configuration = new ConfigurationBuilder().Build();
        using var inherited = new ProcessEnvironment();
        inherited.Apply(configuration, new Dictionary<string, string>
        {
            ["NO_COLOR"] = "",
            ["TERM"] = "xterm-256color",
        });
        inherited.Apply(configuration, new Dictionary<string, string>
        {
            [name] = inheritedDisablesColor ? disablingValue : enablingValue,
        });
        Assert.Equal(inheritedDisablesColor, DevProxyCommand.NoColor);

        foreach (var value in new[] { disablingValue, enablingValue, disablingValue, null })
        {
            var values = new Dictionary<string, string?>();
            if (value is not null)
            {
                values[$"env:{name}"] = value;
            }
            var config = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
            using var environment = new ProcessEnvironment();
            environment.Apply(config, null);
            Assert.Equal(value is null ? inheritedDisablesColor : value == disablingValue, DevProxyCommand.NoColor);
        }

        Assert.Equal(inheritedDisablesColor, DevProxyCommand.NoColor);
    }

    [Theory]
    [InlineData("NO_COLOR", "", "1", true)]
    [InlineData("TERM", "dumb", "xterm-256color", false)]
    public void NoColor_ConfigAndCommandLineOverrides_CommandLineWinsAcrossRuns(
        string name, string configValue, string commandLineValue, bool expected)
    {
        using var inherited = new ProcessEnvironment();
        inherited.Apply(new ConfigurationBuilder().Build(), new Dictionary<string, string>
        {
            ["NO_COLOR"] = "",
            ["TERM"] = "xterm-256color",
        });
        foreach (var value in new[] { configValue, commandLineValue })
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { [$"env:{name}"] = value })
                .Build();
            using var environment = new ProcessEnvironment();
            environment.Apply(configuration, new Dictionary<string, string> { [name] = commandLineValue });
            Assert.Equal(expected, DevProxyCommand.NoColor);
        }
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", "xterm-256color")]
    [InlineData("1", "dumb")]
    public void ShouldDisableColor_ExplicitOption_WinsOverEnvironmentValues(string? noColor, string? term)
    {
        Assert.True(DevProxyCommand.ShouldDisableColor(["--no-color"], noColor, term));
    }

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
