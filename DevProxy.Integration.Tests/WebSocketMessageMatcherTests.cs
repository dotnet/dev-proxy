// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Text.Json.Nodes;
using DevProxy.Plugins.Mocking;
using Xunit;

namespace DevProxy.Integration.Tests;

/// <summary>
/// Pure unit coverage for <see cref="WebSocketMessageMatcher"/> — the bodyFragment /
/// bodyRegex / bodyJson operators, their precedence, plus the catch-all and
/// malformed-input behavior — without a live socket.
/// </summary>
public sealed class WebSocketMessageMatcherTests
{
    [Theory]
    [InlineData("ell", "hello", true)]      // substring
    [InlineData("ELL", "hello", true)]      // case-insensitive
    [InlineData("xyz", "hello", false)]
    public void BodyFragment_IsCaseInsensitiveSubstring(string fragment, string message, bool expected) =>
        Assert.Equal(expected, WebSocketMessageMatcher.Matches(
            new WebSocketMessageMatch { BodyFragment = fragment }, message));

    [Theory]
    [InlineData("^h.*o$", "hello", true)]
    [InlineData("^\\d+$", "12345", true)]
    [InlineData("^\\d+$", "12a45", false)]
    public void BodyRegex_MatchesPattern(string pattern, string message, bool expected) =>
        Assert.Equal(expected, WebSocketMessageMatcher.Matches(
            new WebSocketMessageMatch { BodyRegex = pattern }, message));

    [Fact]
    public void BodyRegex_MalformedPattern_DoesNotThrow_AndDoesNotMatch() =>
        Assert.False(WebSocketMessageMatcher.Matches(
            new WebSocketMessageMatch { BodyRegex = "(unclosed" }, "anything"));

    [Theory]
    [InlineData("""{ "a": 1, "b": 2 }""", """{"b":2,"a":1}""", true)]   // order-insensitive
    [InlineData("""{ "a": 1 }""", """{ "a": 2 }""", false)]              // value differs
    [InlineData("""[1, 2, 3]""", """[1,2,3]""", true)]                   // arrays, whitespace
    public void BodyJson_ComparesStructurally(string expectedJson, string message, bool expected) =>
        Assert.Equal(expected, WebSocketMessageMatcher.Matches(
            new WebSocketMessageMatch { BodyJson = JsonNode.Parse(expectedJson) }, message));

    [Fact]
    public void BodyJson_InvalidInboundJson_DoesNotThrow_AndDoesNotMatch() =>
        Assert.False(WebSocketMessageMatcher.Matches(
            new WebSocketMessageMatch { BodyJson = JsonNode.Parse("""{ "a": 1 }""") }, "not json"));

    [Fact]
    public void Precedence_BodyJson_WinsOverRegexAndFragment() =>
        // bodyJson is set and matches; bodyRegex/bodyFragment would NOT match the raw text,
        // proving only the highest-precedence criterion is evaluated.
        Assert.True(WebSocketMessageMatcher.Matches(
            new WebSocketMessageMatch
            {
                BodyJson = JsonNode.Parse("""{ "a": 1 }"""),
                BodyRegex = "^nope$",
                BodyFragment = "zzz",
            },
            """{"a":1}"""));

    [Fact]
    public void Precedence_BodyRegex_WinsOverFragment() =>
        Assert.True(WebSocketMessageMatcher.Matches(
            new WebSocketMessageMatch { BodyRegex = "^hello$", BodyFragment = "zzz" }, "hello"));

    [Fact]
    public void NullMatch_IsCatchAll() =>
        Assert.True(WebSocketMessageMatcher.Matches(null, "literally anything"));

    [Fact]
    public void NoCriteriaSet_IsCatchAll() =>
        Assert.True(WebSocketMessageMatcher.Matches(new WebSocketMessageMatch(), "anything"));
}
