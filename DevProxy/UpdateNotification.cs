// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Abstractions.Proxy;
using DevProxy.Abstractions.Utils;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DevProxy;

sealed class ReleaseInfo
{
    [JsonPropertyName("name")]
    public string? Version { get; set; }
    [JsonPropertyName("html_url")]
    public string? Url { get; set; }

    public ReleaseInfo()
    {
    }
}

sealed class UpdateNotification(HttpClient httpClient)
{
    private readonly HttpClient _httpClient = httpClient;
    private const string releasesUrl = "https://aka.ms/devproxy/releases";

    /// <summary>
    /// Checks if a new version of the proxy is available.
    /// </summary>
    /// <returns>Instance of ReleaseInfo if a new version is available and null if the current version is the latest</returns>
    public async Task<ReleaseInfo?> CheckForNewVersionAsync(ReleaseType releaseType)
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
            if (ProxyUtils.CompareSemVer(currentVersion, latestReleaseVersion) < 0)
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

    private async Task<ReleaseInfo?> GetLatestReleaseAsync(ReleaseType releaseType)
    {
        // GitHub API requires user agent to be set
        _httpClient.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("dev-proxy", ProxyUtils.ProductVersion));
        var response = await _httpClient.GetStringAsync(new Uri(releasesUrl));
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
                (release.Version.Contains('-', StringComparison.OrdinalIgnoreCase) && releaseType != ReleaseType.Beta))
            {
                continue;
            }

            return release;
        }

        return null;
    }
}
