// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Text.RegularExpressions;

namespace DevProxy.Abstractions.Proxy;

/// <summary>
/// Derives a <b>host-only</b> match pattern from a <c>urlsToWatch</c> URL regex. Both
/// proxy engines need to decide, at CONNECT time, whether a host is watched (→ decrypt)
/// using only the host (the CONNECT authority carries no path), so they both reduce the
/// richer URL patterns to host patterns the same way. This is the single shared
/// implementation so the two engines can't drift.
///
/// <code>
///   ^https://api\.contoso\.com/.*$        (urlsToWatch regex)
///        │ unescape, trim ^ $, .* → *
///        ▼
///   https://api.contoso.com/*
///        │ strip scheme (take authority up to first '/')
///        ▼
///   api.contoso.com[:port]
///        │ strip :port
///        ▼
///   api.contoso.com
///        │ escape, * → .*, anchor
///        ▼
///   ^api\.contoso\.com$                    (host regex)
/// </code>
/// </summary>
public static class WatchedHostExtractor
{
    /// <summary>
    /// Builds an anchored, case-insensitive host regex from a watched-URL regex.
    /// </summary>
    public static Regex ToHostRegex(Regex urlRegex)
    {
        ArgumentNullException.ThrowIfNull(urlRegex);

        var pattern = Regex.Unescape(urlRegex.ToString())
            .Trim('^', '$')
            .Replace(".*", "*", StringComparison.OrdinalIgnoreCase);

        string host;
        if (pattern.Contains("://", StringComparison.OrdinalIgnoreCase))
        {
            // Scheme present: take the authority (everything up to the first '/').
            var chunks = pattern.Split("://");
            var slash = chunks[1].IndexOf('/', StringComparison.OrdinalIgnoreCase);
            host = slash < 0 ? chunks[1] : chunks[1][..slash];
        }
        else
        {
            // No scheme: the whole pattern is treated as a host name.
            host = pattern;
        }

        // Drop a trailing :port — matching is on host only.
        var portPos = host.IndexOf(':', StringComparison.OrdinalIgnoreCase);
        if (portPos > 0)
        {
            host = host[..portPos];
        }

        var regexString = Regex.Escape(host).Replace("\\*", ".*", StringComparison.OrdinalIgnoreCase);
        return new Regex($"^{regexString}$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    }
}
