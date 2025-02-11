// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.DevProxy.Abstractions;

namespace Microsoft.DevProxy;

internal class ReleaseInfo
{
    [JsonPropertyName("name")]
    public string? Version { get; set; }
    [JsonPropertyName("html_url")]
    public string? Url { get; set; }

    public ReleaseInfo()
    {
    }
}

internal static class UpdateNotification
{
    private static readonly string releasesUrl = "https://aka.ms/devproxy/releases";

    /// <summary>
    /// Checks if a new version of the proxy is available.
    /// </summary>
    /// <returns>Instance of ReleaseInfo if a new version is available and null if the current version is the latest</returns>
    public static async Task<ReleaseInfo?> CheckForNewVersionAsync(ReleaseType releaseType)
    {
        try
        {
            var latestRelease = await GetLatestReleaseAsync(releaseType);
            if (latestRelease == null || latestRelease.Version == null)
            {
                return null;
            }

            var latestReleaseVersion = latestRelease.Version;
            var currentVersion = ProxyUtils.ProductVersion;

            // -1 = latest release is greater
            // 0 = versions are equal
            // 1 = current version is greater
            if (CompareSemVer(currentVersion, latestReleaseVersion) < 0)
            {
                return latestRelease;
            }
            else
            {
                return null;
            }
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Compares two semantic versions strings.
    /// </summary>
    /// <param name="a">ver1</param>
    /// <param name="b">ver2</param>
    /// <returns>Returns 0 if the versions are equal, -1 if a is less than b, and 1 if a is greater than b.</returns>
    private static int CompareSemVer(string? a, string? b)
    {
        if (a == null && b == null)
        {
            return 0;
        }
        else if (a == null)
        {
            return -1;
        }
        else if (b == null)
        {
            return 1;
        }

        if (a.StartsWith('v'))
        {
            a = a[1..];
        }
        if (b.StartsWith('v'))
        {
            b = b[1..];
        }

        var aParts = a.Split('-');
        var bParts = b.Split('-');

        var aVersion = new Version(aParts[0]);
        var bVersion = new Version(bParts[0]);

        var compare = aVersion.CompareTo(bVersion);
        if (compare != 0)
        {
            // if the versions are different, return the comparison result
            return compare;
        }

        // if the versions are the same, compare the prerelease tags
        if (aParts.Length == 1 && bParts.Length == 1)
        {
            // if both versions are stable, they are equal
            return 0;
        }
        else if (aParts.Length == 1)
        {
            // if a is stable and b is not, a is greater
            return 1;
        }
        else if (bParts.Length == 1)
        {
            // if b is stable and a is not, b is greater
            return -1;
        }
        else if (aParts[1] == bParts[1])
        {
            // if both versions are prerelease and the tags are the same, they are equal
            return 0;
        }
        else {
            // if both versions are prerelease, b is greater
            return -1;
        }
    }

    private static async Task<ReleaseInfo?> GetLatestReleaseAsync(ReleaseType releaseType)
    {
        var http = new HttpClient();
        // GitHub API requires user agent to be set
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("dev-proxy", ProxyUtils.ProductVersion));
        var response = await http.GetStringAsync(releasesUrl);
        var releases = JsonSerializer.Deserialize<ReleaseInfo[]>(response, ProxyUtils.JsonSerializerOptions);

        if (releases == null || releaseType == ReleaseType.None)
        {
            return null;
        }

        // we assume releases are sorted descending by their creation date
        foreach (var release in releases)
        {
            // skip preview releases
            if (release.Version == null ||
                (release.Version.Contains('-') && releaseType != ReleaseType.Beta))
            {
                continue;
            }

            return release;
        }

        return null;
    }
}
