// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Plugins.Mocking;
using Xunit;

namespace DevProxy.Integration.Tests;

/// <summary>
/// Pure unit coverage for <see cref="WebSocketMessageMatcher"/> — the four match
/// operators plus the catch-all / malformed-input behavior — without a live socket.
/// </summary>
public sealed class WebSocketMessageMatcherTests
{
    [Theory]
    [InlineData("hello", "hello", true)]    // exact, case-sensitive
    [InlineData("hello", "Hello", false)]   // case matters for Equals
    [InlineData("hello", "hell", false)]    // not a substring match
    public void Equals_IsOrdinalAndCaseSensitive(string body, string message, bool expected) =>
        Assert.Equal(expected, Match(body, WebSocketMatchType.Equals, message));

    [Theory]
    [InlineData("ell", "hello", true)]      // substring
    [InlineData("ELL", "hello", true)]      // case-insensitive
    [InlineData("xyz", "hello", false)]
    public void Contains_IsCaseInsensitiveSubstring(string body, string message, bool expected) =>
        Assert.Equal(expected, Match(body, WebSocketMatchType.Contains, message));

    [Theory]
    [InlineData("^h.*o$", "hello", true)]
    [InlineData("^\\d+$", "12345", true)]
    [InlineData("^\\d+$", "12a45", false)]
    public void Regex_MatchesPattern(string body, string message, bool expected) =>
        Assert.Equal(expected, Match(body, WebSocketMatchType.Regex, message));

    [Fact]
    public void Regex_MalformedPattern_DoesNotThrow_AndDoesNotMatch() =>
        Assert.False(Match("(unclosed", WebSocketMatchType.Regex, "anything"));

    [Theory]
    [InlineData("""{ "a": 1, "b": 2 }""", """{"b":2,"a":1}""", true)]   // order-insensitive
    [InlineData("""{ "a": 1 }""", """{ "a": 2 }""", false)]              // value differs
    [InlineData("""[1, 2, 3]""", """[1,2,3]""", true)]                   // arrays, whitespace
    public void Json_ComparesStructurally(string body, string message, bool expected) =>
        Assert.Equal(expected, Match(body, WebSocketMatchType.Json, message));

    [Fact]
    public void Json_InvalidJson_DoesNotThrow_AndDoesNotMatch() =>
        Assert.False(Match("""{ "a": 1 }""", "not json", WebSocketMatchType.Json));

    [Fact]
    public void NullMatch_IsCatchAll() =>
        Assert.True(WebSocketMessageMatcher.Matches(null, "literally anything"));

    [Fact]
    public void NullBody_IsCatchAll() =>
        Assert.True(WebSocketMessageMatcher.Matches(
            new WebSocketMessageMatch { Body = null, MatchType = WebSocketMatchType.Equals }, "anything"));

    private static bool Match(string body, WebSocketMatchType type, string message) =>
        WebSocketMessageMatcher.Matches(new WebSocketMessageMatch { Body = body, MatchType = type }, message);

    private static bool Match(string body, string message, WebSocketMatchType type) =>
        Match(body, type, message);
}
