// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Proxy;
using DevProxy.Commands;
using DevProxy.State;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace DevProxy.Tests;

[CollectionDefinition("management-environment", DisableParallelization = true)]
public class ManagementEnvironmentDefinition;

[Collection("management-environment")]
public class StateManagerEnvironmentTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ConfigFile_AfterHomeOverride_RetainsInitiallyResolvedPath(bool stdio)
    {
        var options = new DevProxyConfigOptions();
        options.ParseOptions(stdio
            ? ["stdio", "-c", "~/.devproxy/devproxyrc.json", "node", "app.js"]
            : ["-c", "~/.devproxy/devproxyrc.json"]);
        var inheritedPath = options.ConfigFile;
        Assert.NotNull(inheritedPath);
        Assert.True(Path.IsPathFullyQualified(inheritedPath));

        var home = Directory.CreateTempSubdirectory("devproxy-config-home-");
        try
        {
            using var environment = new ProcessEnvironment();
            environment.Apply(new ConfigurationBuilder().Build(), new Dictionary<string, string>
            {
                ["HOME"] = home.FullName,
            });
            Assert.Equal(inheritedPath, options.ConfigFile);
        }
        finally
        {
            home.Delete();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ManagementPaths_ConfigOrCommandLineEnvironmentOverrides_RetainInheritedLocation(bool useCommandLine)
    {
        var inheritedFolder = StateManager.GetConfigFolder();
        var overridesFolder = Directory.CreateTempSubdirectory("devproxy-env-");
        var variables = new Dictionary<string, string>
        {
            ["HOME"] = overridesFolder.FullName,
            ["XDG_CONFIG_HOME"] = overridesFolder.FullName,
            ["LOCALAPPDATA"] = overridesFolder.FullName,
        };
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(useCommandLine
                ? []
                : variables.Select(variable =>
                    new KeyValuePair<string, string?>($"env:{variable.Key}", variable.Value)))
            .Build();

        try
        {
            using (var environment = new ProcessEnvironment())
            {
                environment.Apply(configuration, useCommandLine ? variables : null);

                foreach (var (name, value) in variables)
                {
                    Assert.Equal(value, Environment.GetEnvironmentVariable(name));
                }
                AssertManagementPaths(inheritedFolder);
            }

            AssertManagementPaths(inheritedFolder);
        }
        finally
        {
            overridesFolder.Delete();
        }
    }

    private static void AssertManagementPaths(string inheritedFolder)
    {
        Assert.Equal(inheritedFolder, StateManager.GetConfigFolder());
        Assert.Equal(Path.Combine(inheritedFolder, "state-1234.json"), StateManager.GetInstanceStateFilePath(1234));
        Assert.Equal(Path.Combine(inheritedFolder, "state.json"), StateManager.GetStateFilePath());
        Assert.Equal(Path.Combine(inheritedFolder, "credentials", "api-1234.token"), ApiSecurity.GetTokenFilePath(1234));
        Assert.Equal(Path.Combine(inheritedFolder, "logs"), StateManager.GetLogsFolder());
        Assert.Equal(StateManager.GetLogsFolder(), Path.GetDirectoryName(StateManager.GenerateLogFilePath()));
    }
}
