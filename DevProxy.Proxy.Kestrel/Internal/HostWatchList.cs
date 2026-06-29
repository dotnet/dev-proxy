// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Abstractions.Proxy;

namespace DevProxy.Proxy.Kestrel.Internal;

/// <summary>
/// Host-level view of the watched-URL set, used to decide at CONNECT time whether
/// to terminate TLS (watched → MITM) or blind-tunnel (non-watched → relay bytes).
/// Host derivation is shared with the Titanium engine via
/// <see cref="WatchedHostExtractor"/> so the two engines match identically.
/// </summary>
internal sealed class HostWatchList
{
    private readonly List<UrlToWatch> _hosts;

    private HostWatchList(List<UrlToWatch> hosts) => _hosts = hosts;

    public static HostWatchList FromUrls(IEnumerable<UrlToWatch> urlsToWatch)
    {
        ArgumentNullException.ThrowIfNull(urlsToWatch);

        var hosts = new List<UrlToWatch>();
        foreach (var urlToWatch in urlsToWatch)
        {
            var regex = WatchedHostExtractor.ToHostRegex(urlToWatch.Url);

            if (!hosts.Exists(h => h.Url.ToString() == regex.ToString()))
            {
                hosts.Add(new UrlToWatch(regex, urlToWatch.Exclude));
            }
        }

        return new HostWatchList(hosts);
    }

    /// <summary>True when the host matches a non-excluded watch entry.</summary>
    public bool IsWatched(string host)
    {
        ArgumentNullException.ThrowIfNull(host);
        var match = _hosts.Find(h => h.Url.IsMatch(host));
        return match is not null && !match.Exclude;
    }
}
