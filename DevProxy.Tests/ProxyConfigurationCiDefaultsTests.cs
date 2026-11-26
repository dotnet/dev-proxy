// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Commands;
using DevProxy.Proxy;
using Microsoft.Extensions.Configuration;
using System.CommandLine;
using Xunit;

namespace DevProxy.Tests;

public class ProxyConfigurationCiDefaultsTests
{
    private static ProxyConfiguration Create(bool isCi, Dictionary<string, string?>? settings = null)
    {
        var configurationRoot = new ConfigurationBuilder()
            .AddInMemoryCollection(settings ?? [])
            .Build();
        return new ProxyConfiguration(configurationRoot, isCi);
    }

    [Fact]
    public void NotCi_KeepsDefaults()
    {
        var config = Create(isCi: false);

        Assert.True(config.AsSystemProxy);
        Assert.True(config.InstallCert);
        Assert.False(config.AsSystemProxyDefaultedForCi);
        Assert.False(config.InstallCertDefaultedForCi);
    }

    [Fact]
    public void Ci_NotSetInConfig_DisablesSystemProxyAndCertInstall()
    {
        var config = Create(isCi: true);

        Assert.False(config.AsSystemProxy);
        Assert.False(config.InstallCert);
        Assert.True(config.AsSystemProxyDefaultedForCi);
        Assert.True(config.InstallCertDefaultedForCi);
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("false", false)]
    public void Ci_SetInConfig_KeepsConfiguredValues(string value, bool expected)
    {
        var config = Create(isCi: true, new()
        {
            ["asSystemProxy"] = value,
            ["installCert"] = value,
        });

        Assert.Equal(expected, config.AsSystemProxy);
        Assert.Equal(expected, config.InstallCert);
        Assert.False(config.AsSystemProxyDefaultedForCi);
        Assert.False(config.InstallCertDefaultedForCi);
    }

    [Fact]
    public void Ci_OnlyAsSystemProxyInConfig_DefaultsInstallCertOnly()
    {
        var config = Create(isCi: true, new() { ["asSystemProxy"] = "true" });

        Assert.True(config.AsSystemProxy);
        Assert.False(config.InstallCert);
        Assert.False(config.AsSystemProxyDefaultedForCi);
        Assert.True(config.InstallCertDefaultedForCi);
    }

    [Fact]
    public void ShouldLogCiDefaults_NotCi_ReturnsFalse()
    {
        Assert.False(DevProxyCommand.ShouldLogCiDefaults(Create(isCi: false), null, null));
    }

    [Fact]
    public void ShouldLogCiDefaults_CiDefaultsApplied_ReturnsTrue()
    {
        Assert.True(DevProxyCommand.ShouldLogCiDefaults(Create(isCi: true), null, null));
    }

    [Fact]
    public void ShouldLogCiDefaults_OneOverriddenOnCommandLine_ReturnsTrue()
    {
        Assert.True(DevProxyCommand.ShouldLogCiDefaults(Create(isCi: true), true, null));
    }

    [Fact]
    public void ShouldLogCiDefaults_BothOverriddenOnCommandLine_ReturnsFalse()
    {
        Assert.False(DevProxyCommand.ShouldLogCiDefaults(Create(isCi: true), true, true));
    }

    private static ParseResult ParseInstallCert(ProxyConfiguration config, params string[] args)
    {
        var asSystemProxyOption = new Option<bool?>(DevProxyCommand.AsSystemProxyOptionName);
        var command = new RootCommand
        {
            asSystemProxyOption,
            DevProxyCommand.CreateInstallCertOption(asSystemProxyOption, config),
        };
        return command.Parse(args);
    }

    [Fact]
    public void InstallCertFalse_Ci_WithoutAsSystemProxy_IsAccepted()
    {
        var result = ParseInstallCert(Create(isCi: true), "--install-cert", "false");

        Assert.Empty(result.Errors);
    }

    [Fact]
    public void InstallCertFalse_AsSystemProxyFalseInConfig_WithoutAsSystemProxy_IsAccepted()
    {
        var config = Create(isCi: false, new() { ["asSystemProxy"] = "false" });

        var result = ParseInstallCert(config, "--install-cert", "false");

        Assert.Empty(result.Errors);
    }

    [Fact]
    public void InstallCertFalse_AsSystemProxyTrueInConfig_IsRejected()
    {
        var config = Create(isCi: true, new() { ["asSystemProxy"] = "true" });

        var result = ParseInstallCert(config, "--install-cert", "false");

        Assert.Contains(result.Errors, e => e.Message.Contains(DevProxyCommand.AsSystemProxyOptionName, StringComparison.Ordinal));
    }

    [Fact]
    public void InstallCertFalse_AsSystemProxyTrueOnCommandLine_IsRejected()
    {
        var result = ParseInstallCert(Create(isCi: true), "--install-cert", "false", "--as-system-proxy", "true");

        Assert.NotEmpty(result.Errors);
    }
}
