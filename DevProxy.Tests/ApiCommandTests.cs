// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Commands;
using Xunit;

namespace DevProxy.Tests;

public class ApiCommandTests
{
    [Fact]
    public void Endpoints_PostProxy_ShowsRecordingBody()
    {
        var endpoint = ApiCommand.Endpoints.Single(e => e.Method == "POST" && e.Path == "/proxy");

        Assert.Contains("body: {\"recording\": true|false}", ApiCommand.FormatDescription(endpoint), StringComparison.Ordinal);
    }

    [Fact]
    public void Endpoints_RootCertificate_ShowsRequiredFormatQuery()
    {
        var endpoint = ApiCommand.Endpoints.Single(e => e.Path == "/proxy/rootCertificate");

        Assert.Contains("query: format=crt", ApiCommand.FormatDescription(endpoint), StringComparison.Ordinal);
    }

    [Fact]
    public void Endpoints_JwtToken_ShowsBody()
    {
        var endpoint = ApiCommand.Endpoints.Single(e => e.Path == "/proxy/jwtToken");

        Assert.Contains("body: {\"name\":", ApiCommand.FormatDescription(endpoint), StringComparison.Ordinal);
    }

    [Fact]
    public void Endpoints_WithoutInput_ShowDescriptionOnly()
    {
        var endpoint = ApiCommand.Endpoints.Single(e => e.Path == "/proxy/stopProxy");

        Assert.Equal("Stop the proxy", ApiCommand.FormatDescription(endpoint));
    }
}
