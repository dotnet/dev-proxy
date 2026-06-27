// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Text.RegularExpressions;
using DevProxy.Abstractions.Proxy;

namespace DevProxy.Proxy.Kestrel.Internal;

/// <summary>
/// Host-level view of the watched-URL set, used to decide at CONNECT time whether
/// to terminate TLS (watched → MITM) or blind-tunnel (non-watched → relay bytes).
///
/// <para>
/// TODO (DRY, cut-over): this host-extraction logic is duplicated from
/// <c>ProxyEngine.LoadHostNamesFromUrls</c>. Consolidate into a single shared
/// helper in <c>DevProxy.Abstractions</c> when the Titanium engine is removed,
/// so there is one implementation with one test suite.
/// </para>
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
            var pattern = Regex.Unescape(urlToWatch.Url.ToString())
                .Trim('^', '$')
                .Replace(".*", "*", StringComparison.OrdinalIgnoreCase);

            string host;
            if (pattern.Contains("://", StringComparison.OrdinalIgnoreCase))
            {
                var chunks = pattern.Split("://");
                var slash = chunks[1].IndexOf('/', StringComparison.OrdinalIgnoreCase);
                host = slash < 0 ? chunks[1] : chunks[1][..slash];
            }
            else
            {
                host = pattern;
            }

            var portPos = host.IndexOf(':', StringComparison.OrdinalIgnoreCase);
            if (portPos > 0)
            {
                host = host[..portPos];
            }

            var regexString = Regex.Escape(host).Replace("\\*", ".*", StringComparison.OrdinalIgnoreCase);
            var regex = new Regex($"^{regexString}$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

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
