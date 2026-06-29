// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Text.RegularExpressions;
using DevProxy.Abstractions.Proxy;
using Xunit;

namespace DevProxy.Abstractions.Tests.Proxy;

public class WatchedHostExtractorTests
{
    // Mirrors PluginServiceExtensions.ConvertToRegex so tests feed the same
    // UrlToWatch regex shape both engines receive at runtime.
    private static Regex ToUrlRegex(string pattern) =>
        new(
            $"^{Regex.Escape(pattern).Replace("\\*", ".*", StringComparison.OrdinalIgnoreCase)}$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static string HostPattern(string urlPattern) =>
        WatchedHostExtractor.ToHostRegex(ToUrlRegex(urlPattern)).ToString();

    [Fact]
    public void ToHostRegex_StripsSchemeAndPath() =>
        Assert.Equal("^api\\.contoso\\.com$", HostPattern("https://api.contoso.com/v1/users"));

    [Fact]
    public void ToHostRegex_HostOnlyPattern_NoScheme() =>
        Assert.Equal("^api\\.contoso\\.com$", HostPattern("api.contoso.com"));

    [Fact]
    public void ToHostRegex_StripsPort() =>
        Assert.Equal("^localhost$", HostPattern("https://localhost:3000/*"));

    [Fact]
    public void ToHostRegex_PreservesWildcardSubdomain() =>
        Assert.Equal("^.*\\.contoso\\.com$", HostPattern("https://*.contoso.com/*"));

    [Fact]
    public void ToHostRegex_GlobalWildcard() =>
        Assert.Equal("^.*$", HostPattern("https://*/*"));

    [Fact]
    public void ToHostRegex_NoPath_NoTrailingSlash() =>
        Assert.Equal("^api\\.contoso\\.com$", HostPattern("https://api.contoso.com"));

    [Theory]
    [InlineData("https://jsonplaceholder.typicode.com/*", "jsonplaceholder.typicode.com", true)]
    [InlineData("https://jsonplaceholder.typicode.com/*", "example.com", false)]
    [InlineData("https://*.contoso.com/*", "api.contoso.com", true)]
    [InlineData("https://*.contoso.com/*", "contoso.net", false)]
    public void ToHostRegex_MatchesExpectedHosts(string urlPattern, string host, bool expected)
    {
        var hostRegex = WatchedHostExtractor.ToHostRegex(ToUrlRegex(urlPattern));
        Assert.Equal(expected, hostRegex.IsMatch(host));
    }

    [Fact]
    public void ToHostRegex_Throws_OnNull() =>
        Assert.Throws<ArgumentNullException>(() => WatchedHostExtractor.ToHostRegex(null!));
}
