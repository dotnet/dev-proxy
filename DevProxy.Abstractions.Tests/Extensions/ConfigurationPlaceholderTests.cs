// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Microsoft.Extensions.Configuration;
using Xunit;

namespace DevProxy.Abstractions.Tests.Extensions;

public sealed class ConfigurationPlaceholderTests : IDisposable
{
    private const string OsVariable = "DEVPROXY_TEST_PLACEHOLDER_OS";
    private readonly string _folder = Directory.CreateTempSubdirectory("devproxy-placeholders").FullName;

    public ConfigurationPlaceholderTests()
    {
        Environment.SetEnvironmentVariable(OsVariable, "from-os");
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(OsVariable, null);
        Directory.Delete(_folder, true);
    }

    [Fact]
    public void AddConfigFile_Json_ResolvesPlaceholders()
    {
        var path = Write("devproxyrc.json", $$"""
        {
          "env": { "FROM_SECTION": "section-${{{OsVariable}}}" },
          "plugin": {
            "os": "${{{OsVariable}}}",
            "alias": "${env:{{OsVariable}}}",
            "section": "${FROM_SECTION}",
            "cli": "${FROM_CLI}",
            "escaped": "$${{{OsVariable}}}",
            "missing": "${DEVPROXY_TEST_PLACEHOLDER_MISSING}",
            "port": "${PORT}"
          }
        }
        """);

        var (config, unresolved) = Build(path, new Dictionary<string, string> { ["FROM_CLI"] = "cli", ["PORT"] = "8000" });

        Assert.Equal("from-os", config["plugin:os"]);
        Assert.Equal("from-os", config["plugin:alias"]);
        Assert.Equal("section-from-os", config["env:FROM_SECTION"]);
        Assert.Equal("section-from-os", config["plugin:section"]);
        Assert.Equal("cli", config["plugin:cli"]);
        Assert.Equal($"${{{OsVariable}}}", config["plugin:escaped"]);
        Assert.Equal("${DEVPROXY_TEST_PLACEHOLDER_MISSING}", config["plugin:missing"]);
        Assert.Equal(8000, config.GetValue<int>("plugin:port"));
        Assert.Equal(["DEVPROXY_TEST_PLACEHOLDER_MISSING"], unresolved);
    }

    [Fact]
    public void AddConfigFile_CliVariablesOverrideEnvSection()
    {
        var path = Write("devproxyrc.json", """
        { "env": { "NAME": "section" }, "value": "${NAME}" }
        """);

        var (config, _) = Build(path, new Dictionary<string, string> { ["NAME"] = "cli" });

        Assert.Equal("cli", config["value"]);
    }

    [Fact]
    public void AddConfigFile_CliVariablesDifferingInCasing_LastValueWins()
    {
        var path = Write("devproxyrc.json", """
        { "value": "${NAME}" }
        """);

        var (config, _) = Build(path, new Dictionary<string, string> { ["NAME"] = "first", ["name"] = "second" });

        Assert.Equal(OperatingSystem.IsWindows() ? "second" : "first", config["value"]);
    }

    [Fact]
    public void AddConfigFile_SkipsInlineRewriteRules()
    {
        var path = Write("devproxyrc.json", $$"""
        {
          "plugins": [
            { "name": "RewritePlugin", "enabled": true, "pluginPath": "plugins/DevProxy.Plugins.dll", "configSection": "myRewrites" }
          ],
          "myRewrites": {
            "rewritesFile": "${{{OsVariable}}}.json",
            "rewrites": [ { "out": { "url": "/users/${{{OsVariable}}}" } } ]
          },
          "other": { "rewrites": [ "${{{OsVariable}}}" ] }
        }
        """);

        var (config, _) = Build(path, null);

        Assert.Equal("from-os.json", config["myRewrites:rewritesFile"]);
        Assert.Equal($"/users/${{{OsVariable}}}", config["myRewrites:rewrites:0:out:url"]);
        Assert.Equal("from-os", config["other:rewrites:0"]);
    }

    [Fact]
    public void AddConfigFile_SkipsInlineRewriteRules_WithPlaceholdersInPluginMetadata()
    {
        var path = Write("devproxyrc.json", $$"""
        {
          "env": { "PLUGIN": "RewritePlugin", "SECTION": "myRewrites" },
          "plugins": [ { "name": "${PLUGIN}", "configSection": "${SECTION}" } ],
          "myRewrites": { "rewrites": [ { "out": { "url": "/users/${{{OsVariable}}}" } } ] }
        }
        """);

        var (config, _) = Build(path, null);

        Assert.Equal("RewritePlugin", config["plugins:0:name"]);
        Assert.Equal("myRewrites", config["plugins:0:configSection"]);
        Assert.Equal($"/users/${{{OsVariable}}}", config["myRewrites:rewrites:0:out:url"]);
    }

    [Fact]
    public void AddConfigFile_Yaml_ResolvesPlaceholders()
    {
        var path = Write("devproxyrc.yaml", $$"""
        env:
          FROM_SECTION: section
        plugin:
          os: ${{{OsVariable}}}
          section: "${FROM_SECTION}"
          list:
            - ${env:{{OsVariable}}}
        """);

        var (config, unresolved) = Build(path, null);

        Assert.Equal("from-os", config["plugin:os"]);
        Assert.Equal("section", config["plugin:section"]);
        Assert.Equal("from-os", config["plugin:list:0"]);
        Assert.Empty(unresolved);
    }

    private string Write(string name, string contents)
    {
        var path = Path.Combine(_folder, name);
        File.WriteAllText(path, contents);
        return path;
    }

    private static (IConfigurationRoot Config, IReadOnlyCollection<string> Unresolved) Build(string path, IReadOnlyDictionary<string, string>? variables)
    {
        var config = new ConfigurationBuilder().AddConfigFile(path, environmentVariables: variables).Build();
        var provider = Assert.Single(config.Providers.OfType<IEnvironmentVariablePlaceholderProvider>());
        return (config, provider.UnresolvedEnvironmentVariables);
    }
}
