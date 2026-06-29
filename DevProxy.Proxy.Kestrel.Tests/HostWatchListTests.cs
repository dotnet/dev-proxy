// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Text.RegularExpressions;
using DevProxy.Abstractions.Proxy;
using DevProxy.Proxy.Kestrel.Internal;

using Xunit;

namespace DevProxy.Proxy.Kestrel.Tests;

public class HostWatchListTests
{
    // Mirrors DevProxy's PluginServiceExtensions.ConvertToRegex so tests exercise the
    // same UrlToWatch shape the engine receives at runtime.
    private static UrlToWatch ToWatch(string pattern)
    {
        var exclude = false;
        if (pattern.StartsWith('!'))
        {
            exclude = true;
            pattern = pattern[1..];
        }

        return new UrlToWatch(
            new Regex($"^{Regex.Escape(pattern).Replace("\\*", ".*", StringComparison.OrdinalIgnoreCase)}$", RegexOptions.Compiled | RegexOptions.IgnoreCase),
            exclude);
    }

    [Fact]
    public void IsWatched_MatchesExactHost()
    {
        var list = HostWatchList.FromUrls([ToWatch("https://jsonplaceholder.typicode.com/*")]);

        Assert.True(list.IsWatched("jsonplaceholder.typicode.com"));
        Assert.False(list.IsWatched("example.com"));
    }

    [Fact]
    public void IsWatched_MatchesWildcardSubdomain()
    {
        var list = HostWatchList.FromUrls([ToWatch("https://*.contoso.com/*")]);

        Assert.True(list.IsWatched("api.contoso.com"));
        Assert.True(list.IsWatched("www.contoso.com"));
        Assert.False(list.IsWatched("contoso.net"));
    }

    [Fact]
    public void IsWatched_StripsPortFromPattern()
    {
        var list = HostWatchList.FromUrls([ToWatch("https://localhost:3000/*")]);

        Assert.True(list.IsWatched("localhost"));
    }

    [Fact]
    public void IsWatched_RespectsExclusion()
    {
        // URL matching is first-match-wins, so the more specific exclusion must be
        // ordered before the broad include (Dev Proxy's documented convention).
        var list = HostWatchList.FromUrls(
        [
            ToWatch("!https://admin.contoso.com/*"),
            ToWatch("https://*.contoso.com/*"),
        ]);

        Assert.True(list.IsWatched("api.contoso.com"));
        Assert.False(list.IsWatched("admin.contoso.com"));
    }

    [Fact]
    public void IsWatched_ExclusionOrderedAfterIncludeHasNoEffect()
    {
        // Documents the order-dependence: an exclusion placed after the broad include
        // never wins, mirroring ProxyUtils.MatchesUrlToWatch (FirstOrDefault).
        var list = HostWatchList.FromUrls(
        [
            ToWatch("https://*.contoso.com/*"),
            ToWatch("!https://admin.contoso.com/*"),
        ]);

        Assert.True(list.IsWatched("admin.contoso.com"));
    }

    [Fact]
    public void IsWatched_GlobalWildcard_MatchesAnyHost()
    {
        var list = HostWatchList.FromUrls([ToWatch("https://*/*")]);

        Assert.True(list.IsWatched("anything.example"));
    }

    [Fact]
    public void FromUrls_Throws_OnNull() =>
        Assert.Throws<ArgumentNullException>(() => HostWatchList.FromUrls(null!));
}
