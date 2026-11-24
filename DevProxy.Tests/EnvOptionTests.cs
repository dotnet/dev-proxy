// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Commands;
using DevProxy.Proxy;
using Microsoft.Extensions.Configuration;

using Xunit;

namespace DevProxy.Tests;

public class EnvOptionTests
{
    [Fact]
    public void Env_ParsesCommandLineValues()
    {
        var options = new DevProxyConfigOptions();
        options.ParseOptions(["--env", "FOO=bar", "-e", "URL=https://x?a=b"]);

        var env = options.Env;

        Assert.NotNull(env);
        Assert.Equal("bar", env["FOO"]);
        Assert.Equal("https://x?a=b", env["URL"]);
    }

    [Fact]
    public void Env_NotSpecified_ReturnsNull()
    {
        var options = new DevProxyConfigOptions();
        options.ParseOptions(["--port", "0"]);

        Assert.Null(options.Env);
    }

    [Fact]
    public void Apply_SetsConfigAndCommandLineValues_CommandLineWins()
    {
        var prefix = $"DEVPROXY_TEST_{Guid.NewGuid():N}_";
        var fromConfig = prefix + "CONFIG";
        var overridden = prefix + "OVERRIDDEN";
        var fromCli = prefix + "CLI";

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"env:{fromConfig}"] = "config",
                [$"env:{overridden}"] = "config",
            })
            .Build();

        try
        {
            using var environment = new ProcessEnvironment();
            environment.Apply(configuration, new Dictionary<string, string>
            {
                [overridden] = "cli",
                [fromCli] = "cli",
            });

            Assert.Equal("config", Environment.GetEnvironmentVariable(fromConfig));
            Assert.Equal("cli", Environment.GetEnvironmentVariable(overridden));
            Assert.Equal("cli", Environment.GetEnvironmentVariable(fromCli));
        }
        finally
        {
            Environment.SetEnvironmentVariable(fromConfig, null);
            Environment.SetEnvironmentVariable(overridden, null);
            Environment.SetEnvironmentVariable(fromCli, null);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("inherited")]
    public void Dispose_BeforeReload_RestoresRemovedOverridesAndPreservesCommandLineValues(string? inherited)
    {
        var prefix = $"DEVPROXY_TEST_{Guid.NewGuid():N}_";
        var removed = prefix + "REMOVED";
        var overridden = prefix + "OVERRIDDEN";
        var cli = new Dictionary<string, string> { [overridden] = "cli" };
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"env:{removed}"] = "config",
                [$"env:{overridden}"] = "config",
            })
            .Build();

        Environment.SetEnvironmentVariable(removed, inherited);
        try
        {
            using (var environment = new ProcessEnvironment())
            {
                environment.Apply(configuration, cli);
                Assert.Equal("config", Environment.GetEnvironmentVariable(removed));
                Assert.Equal("cli", Environment.GetEnvironmentVariable(overridden));
            }

            var reloadedConfiguration = new ConfigurationBuilder().Build();
            using (var environment = new ProcessEnvironment())
            {
                environment.Apply(reloadedConfiguration, cli);
                Assert.Equal(inherited, Environment.GetEnvironmentVariable(removed));
                Assert.Equal("cli", Environment.GetEnvironmentVariable(overridden));
            }

            Assert.Equal(inherited, Environment.GetEnvironmentVariable(removed));
            Assert.Null(Environment.GetEnvironmentVariable(overridden));
        }
        finally
        {
            Environment.SetEnvironmentVariable(removed, null);
            Environment.SetEnvironmentVariable(overridden, null);
        }
    }

    [Fact]
    public void Dispose_WhenApplyingOverridesFails_RestoresEarlierOverrides()
    {
        var name = $"DEVPROXY_TEST_{Guid.NewGuid():N}";
        var configuration = new ConfigurationBuilder().Build();

        Assert.Throws<ArgumentException>(() =>
        {
            using var environment = new ProcessEnvironment();
            environment.Apply(configuration, new Dictionary<string, string>
            {
                [name] = "config",
                ["invalid=name"] = "invalid",
            });
        });

        Assert.Null(Environment.GetEnvironmentVariable(name));
    }
}
