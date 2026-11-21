// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Abstractions.Proxy;
using DevProxy.Abstractions.Utils;
using DevProxy.Plugins.Reporters;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DevProxy.Integration.Tests;

/// <summary>
/// Verifies that reporters honour <see cref="IProxyConfiguration.ReportsFolder"/>
/// and fall back to the current working directory when it isn't set.
/// </summary>
public sealed class ReporterReportsFolderTests
{
    private static RecordingArgs Recording() =>
        new([])
        {
            GlobalData = new()
            {
                [ProxyUtils.ReportsKey] = new Dictionary<string, object>
                {
                    ["TestPlugin"] = new { Value = 1 }
                }
            },
        };

    private static JsonReporter CreateReporter(string? reportsFolder) =>
        new(NullLogger<JsonReporter>.Instance, new HashSet<UrlToWatch>(), new TestProxyConfiguration { ReportsFolder = reportsFolder });

    [Fact]
    public async Task WritesReportToReportsFolder_CreatingItWhenMissing()
    {
        var root = Directory.CreateTempSubdirectory("devproxy-reports-");
        var cwd = Directory.CreateTempSubdirectory("devproxy-cwd-");
        var originalCwd = Directory.GetCurrentDirectory();
        Directory.SetCurrentDirectory(cwd.FullName);
        try
        {
            var reportsFolder = Path.Combine(root.FullName, "nested", "reports");
            var reporter = CreateReporter(reportsFolder);

            await reporter.AfterRecordingStopAsync(Recording(), CancellationToken.None);

            Assert.True(File.Exists(Path.Combine(reportsFolder, "TestPlugin_JsonReporter.json")));
            Assert.Empty(Directory.GetFiles(cwd.FullName));
        }
        finally
        {
            Directory.SetCurrentDirectory(originalCwd);
            root.Delete(true);
            cwd.Delete(true);
        }
    }

    [Fact]
    public async Task AppendsTimestamp_WhenReportExistsInReportsFolder()
    {
        var root = Directory.CreateTempSubdirectory("devproxy-reports-");
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root.FullName, "TestPlugin_JsonReporter.json"), "{}");
            var reporter = CreateReporter(root.FullName);

            await reporter.AfterRecordingStopAsync(Recording(), CancellationToken.None);

            Assert.Equal(2, Directory.GetFiles(root.FullName, "TestPlugin_JsonReporter*.json").Length);
        }
        finally
        {
            root.Delete(true);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task WritesReportToWorkingDirectory_WhenReportsFolderNotSet(string? reportsFolder)
    {
        var cwd = Directory.CreateTempSubdirectory("devproxy-cwd-");
        var originalCwd = Directory.GetCurrentDirectory();
        Directory.SetCurrentDirectory(cwd.FullName);
        try
        {
            var reporter = CreateReporter(reportsFolder);

            await reporter.AfterRecordingStopAsync(Recording(), CancellationToken.None);

            Assert.True(File.Exists(Path.Combine(cwd.FullName, "TestPlugin_JsonReporter.json")));
        }
        finally
        {
            Directory.SetCurrentDirectory(originalCwd);
            cwd.Delete(true);
        }
    }
}
