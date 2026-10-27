// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Abstractions.Proxy;
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
}
