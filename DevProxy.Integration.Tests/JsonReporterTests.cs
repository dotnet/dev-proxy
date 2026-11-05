// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Abstractions.Proxy;
using DevProxy.Abstractions.Utils;
using DevProxy.Plugins.Reporters;
using DevProxy.Plugins.Reporting;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DevProxy.Integration.Tests;

public sealed class JsonReporterTests
{
    private sealed class TestJsonReporter() : JsonReporter(NullLogger<JsonReporter>.Instance, new HashSet<UrlToWatch>())
    {
        public string? Report(string key, object value) => GetReport(new(key, value));
    }

    [Fact]
    public void FileExtension_IsDeterminedPerReport()
    {
        var reporter = new TestJsonReporter();

        _ = reporter.Report("UrlDiscoveryPlugin", new UrlDiscoveryPluginReport { Data = ["https://api.contoso.com/*"] });
        Assert.Equal(".jsonc", reporter.FileExtension);

        _ = reporter.Report("OtherPlugin", new { Value = 1 });
        Assert.Equal(".json", reporter.FileExtension);
    }

    [Fact]
    public async Task AfterRecordingStop_RecordsAbsolutePathsOfWrittenReports()
    {
        var reporter = new TestJsonReporter();
        var args = new RecordingArgs([])
        {
            GlobalData = new()
            {
                [ProxyUtils.ReportsKey] = new Dictionary<string, object>
                {
                    ["SomePlugin"] = new { Value = 1 },
                    ["OtherPlugin"] = new { Value = 2 },
                },
            },
        };

        var dir = Directory.CreateTempSubdirectory("devproxy-reporter-");
        var originalCwd = Directory.GetCurrentDirectory();
        Directory.SetCurrentDirectory(dir.FullName);
        try
        {
            await reporter.AfterRecordingStopAsync(args, CancellationToken.None);
        }
        finally
        {
            Directory.SetCurrentDirectory(originalCwd);
        }

        try
        {
            var reportFiles = args.ReportFiles;
            Assert.Equal(2, reportFiles.Count);
            Assert.All(reportFiles, f =>
            {
                Assert.True(Path.IsPathFullyQualified(f));
                Assert.True(File.Exists(f));
            });
            Assert.Contains(reportFiles, f => Path.GetFileName(f) == "SomePlugin_JsonReporter.json");
            Assert.Contains(reportFiles, f => Path.GetFileName(f) == "OtherPlugin_JsonReporter.json");
        }
        finally
        {
            dir.Delete(true);
        }
    }
}
