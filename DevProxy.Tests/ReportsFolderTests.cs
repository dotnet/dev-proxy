// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Commands;
using DevProxy.Proxy;
using Microsoft.Extensions.Configuration;

using Xunit;

namespace DevProxy.Tests;

public class ReportsFolderTests
{
    [Fact]
    public void ReportsFolder_FromConfigFile_ResolvesRelativeToConfigFile()
    {
        var configDir = Directory.CreateTempSubdirectory("devproxy-config-");
        try
        {
            var configPath = Path.Combine(configDir.FullName, "devproxyrc.json");
            File.WriteAllText(configPath, """{ "reportsFolder": "out/reports" }""");
            var configuration = new ConfigurationBuilder().AddJsonFile(configPath).Build();

            var proxyConfiguration = new ProxyConfiguration(configuration);

            Assert.Equal(
                Path.Combine(Path.GetDirectoryName(proxyConfiguration.ConfigFile)!, "out", "reports"),
                proxyConfiguration.ReportsFolder);
        }
        finally
        {
            configDir.Delete(true);
        }
    }

    [Fact]
    public void ReportsFolder_FromConfigFile_KeepsAbsolutePath()
    {
        var configDir = Directory.CreateTempSubdirectory("devproxy-config-");
        try
        {
            var reportsFolder = Path.Combine(Path.GetTempPath(), "devproxy-absolute-reports");
            var configPath = Path.Combine(configDir.FullName, "devproxyrc.json");
            File.WriteAllText(configPath, $$"""{ "reportsFolder": {{System.Text.Json.JsonSerializer.Serialize(reportsFolder)}} }""");
            var configuration = new ConfigurationBuilder().AddJsonFile(configPath).Build();

            var proxyConfiguration = new ProxyConfiguration(configuration);

            Assert.Equal(Path.GetFullPath(reportsFolder), proxyConfiguration.ReportsFolder);
        }
        finally
        {
            configDir.Delete(true);
        }
    }

    [Fact]
    public void ReportsFolder_NotConfigured_IsNull()
    {
        var configDir = Directory.CreateTempSubdirectory("devproxy-config-");
        try
        {
            var configPath = Path.Combine(configDir.FullName, "devproxyrc.json");
            File.WriteAllText(configPath, "{}");
            var configuration = new ConfigurationBuilder().AddJsonFile(configPath).Build();

            var proxyConfiguration = new ProxyConfiguration(configuration);

            Assert.Null(proxyConfiguration.ReportsFolder);
        }
        finally
        {
            configDir.Delete(true);
        }
    }

    [Fact]
    public void ReportDirOption_ResolvesRelativeToWorkingDirectory()
    {
        var expected = Path.Combine(Directory.GetCurrentDirectory(), "reports");

        Assert.Equal(expected, DevProxyCommand.ResolveReportDirOption("reports"));
    }

    [Fact]
    public void ReportDirOption_ExpandsHomeDirectory()
    {
        var expected = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "reports");

        Assert.Equal(expected, DevProxyCommand.ResolveReportDirOption($"~{Path.DirectorySeparatorChar}reports"));
    }
}
