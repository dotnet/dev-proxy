// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Abstractions.Utils;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace DevProxy.Plugins.Utils;

/// <summary>
/// Matches URLs against wildcard (<c>*</c>) patterns from mock files, building each
/// pattern's regex once instead of on every request. The static
/// <see cref="Regex.IsMatch(string, string, RegexOptions)"/> cache holds only 15
/// patterns, so with more wildcard mocks than that every lookup rebuilt the regex.
///
/// <para>
/// Regexes are keyed weakly on the pattern string instance (the mock's URL), so
/// they live as long as the loaded mock does and are dropped when mocks reload.
/// </para>
/// </summary>
internal static class WildcardUrlPattern
{
    private static readonly ConditionalWeakTable<string, Regex> s_regexes = [];
    private static readonly ConditionalWeakTable<string, Regex> s_webSocketRegexes = [];

    public static bool IsMatch(string url, string pattern) =>
        s_regexes.GetValue(pattern, static p => Create(p)).IsMatch(url);

    /// <summary>
    /// Like <see cref="IsMatch"/>, but normalizes a <c>ws(s)://</c> scheme in the
    /// pattern to <c>http(s)://</c> first (see <see cref="ProxyUtils.NormalizeWebSocketScheme"/>).
    /// </summary>
    public static bool IsWebSocketMatch(string url, string pattern) =>
        s_webSocketRegexes.GetValue(pattern, static p => Create(ProxyUtils.NormalizeWebSocketScheme(p))).IsMatch(url);

    private static Regex Create(string pattern) =>
        new(ProxyUtils.PatternToRegex(pattern), RegexOptions.IgnoreCase);
}
