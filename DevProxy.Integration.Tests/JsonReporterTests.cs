// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Abstractions.Plugins;
using DevProxy.Abstractions.Proxy;
using DevProxy.Plugins.Reporters;
using DevProxy.Plugins.Reporting;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json;
using Xunit;

namespace DevProxy.Integration.Tests;

public sealed class JsonReporterTests
{
    private sealed class TestJsonReporter() : JsonReporter(NullLogger<JsonReporter>.Instance, new HashSet<UrlToWatch>())
    {
        public string? Report(string key, object value) => GetReport(new(key, value));
    }

    private sealed class TestJsonReport : IJsonReport
    {
        public string FileExtension => ".jsonc";
        public object ToJson() => new { Value = 1 };
    }

    [Fact]
    public void FileExtension_IsDeterminedPerReport()
    {
        var reporter = new TestJsonReporter();

        _ = reporter.Report("CustomPlugin", new TestJsonReport());
        Assert.Equal(".jsonc", reporter.FileExtension);

        _ = reporter.Report("OtherPlugin", new { Value = 1 });
        Assert.Equal(".json", reporter.FileExtension);
    }

    [Fact]
    public void UrlDiscoveryReport_IsStrictJson()
    {
        var reporter = new TestJsonReporter();

        var json = reporter.Report("UrlDiscoveryPlugin", new UrlDiscoveryPluginReport
        {
            Data = ["https://api.contoso.com/orders", "https://api.contoso.com/users"]
        });

        Assert.Equal(".json", reporter.FileExtension);
        Assert.NotNull(json);
        using var doc = JsonDocument.Parse(json);
        var urls = doc.RootElement.GetProperty("urlsToWatch")
            .EnumerateArray()
            .Select(e => e.GetString()!)
            .ToArray();
        Assert.Equal(["https://api.contoso.com/orders", "https://api.contoso.com/users"], urls);
    }
}
