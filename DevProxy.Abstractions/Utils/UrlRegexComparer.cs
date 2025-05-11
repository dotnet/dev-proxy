// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Text.RegularExpressions;

namespace DevProxy.Abstractions.Utils;

enum UrlRegexComparisonResult
{
    /// <summary>
    /// The first pattern is broader than the second pattern.
    /// </summary>
    FirstPatternBroader,

    /// <summary>
    /// The second pattern is broader than the first pattern.
    /// </summary>
    SecondPatternBroader,

    /// <summary>
    /// The patterns are equivalent.
    /// </summary>
    PatternsEquivalent,

    /// <summary>
    /// The patterns are mutually exclusive.
    /// </summary>
    PatternsMutuallyExclusive
}

sealed class UrlRegexComparer
{
    /// <summary>
    /// Compares two URL patterns and returns a value indicating their
    //  relationship.
    /// </summary>
    /// <param name="pattern1">First URL pattern</param>
    /// <param name="pattern2">Second URL pattern</param>
    /// <returns>1 when the first pattern is broader; -1 when the second pattern
    /// is broader or patterns are mutually exclusive; 0 when the patterns are
    /// equal</returns>
    public static UrlRegexComparisonResult CompareRegexPatterns(string pattern1, string pattern2)
    {
        var regex1 = new Regex(ProxyUtils.PatternToRegex(pattern1));
        var regex2 = new Regex(ProxyUtils.PatternToRegex(pattern2));

        // Generate test URLs based on patterns
        var testUrls = GenerateTestUrls(pattern1, pattern2);

        var matches1 = testUrls.Where(url => regex1.IsMatch(url)).ToList();
        var matches2 = testUrls.Where(url => regex2.IsMatch(url)).ToList();

        var pattern1MatchesAll = matches2.All(regex1.IsMatch);
        var pattern2MatchesAll = matches1.All(regex2.IsMatch);

        if (pattern1MatchesAll && !pattern2MatchesAll)
        {
            // Pattern 1 is broader
            return UrlRegexComparisonResult.FirstPatternBroader;
        }
        else if (pattern2MatchesAll && !pattern1MatchesAll)
        {
            // Pattern 2 is broader
            return UrlRegexComparisonResult.SecondPatternBroader;
        }
        else if (pattern1MatchesAll && pattern2MatchesAll)
        {
            // Patterns are equivalent
            return UrlRegexComparisonResult.PatternsEquivalent;
        }
        else
        {
            // Patterns have different matching sets
            return UrlRegexComparisonResult.PatternsMutuallyExclusive;
        }
    }

    private static List<string> GenerateTestUrls(string pattern1, string pattern2)
    {
        var urls = new HashSet<string>();

        // Extract domains and paths from patterns
        var domains = ExtractDomains(pattern1)
            .Concat(ExtractDomains(pattern2))
            .Distinct()
            .ToList();

        var paths = ExtractPaths(pattern1)
            .Concat(ExtractPaths(pattern2))
            .Distinct()
            .ToList();

        // Generate combinations
        foreach (var domain in domains)
        {
            foreach (var path in paths)
            {
                _ = urls.Add($"https://{domain}/{path}");
            }

            // Add variants
            _ = urls.Add($"https://{domain}/");
            _ = urls.Add($"https://sub.{domain}/path");
            _ = urls.Add($"https://other-{domain}/different");
        }

        return [.. urls];
    }

    private static HashSet<string> ExtractDomains(string pattern)
    {
        var domains = new HashSet<string>();

        // Extract literal domains
        var domainMatch = Regex.Match(Regex.Unescape(pattern), @"https://([^/\s]+)");
        if (domainMatch.Success)
        {
            var domain = domainMatch.Groups[1].Value;
            if (!domain.Contains(".*", StringComparison.OrdinalIgnoreCase))
            {
                _ = domains.Add(domain);
            }
        }

        // Add test domains
        _ = domains.Add("example.com");
        _ = domains.Add("test.com");

        return domains;
    }

    private static HashSet<string> ExtractPaths(string pattern)
    {
        var paths = new HashSet<string>();

        // Extract literal paths
        var pathMatch = Regex.Match(pattern, @"https://[^/]+(/[^/\s]+)");
        if (pathMatch.Success)
        {
            var path = pathMatch.Groups[1].Value;
            if (!path.Contains(".*", StringComparison.OrdinalIgnoreCase))
            {
                _ = paths.Add(path.TrimStart('/'));
            }
        }

        // Add test paths
        _ = paths.Add("api");
        _ = paths.Add("users");
        _ = paths.Add("path1/path2");

        return paths;
    }
}