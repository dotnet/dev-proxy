// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace DevProxy.Plugins.Mocking;

/// <summary>Whether a scripted outbound message is text or (base64) binary.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<WebSocketMessageType>))]
public enum WebSocketMessageType
{
    /// <summary>The body is sent verbatim as a UTF-8 text message.</summary>
    Text,
    /// <summary>The body is a base64 string, decoded and sent as a binary message.</summary>
    Binary,
}

/// <summary>A single scripted message the mock server sends to the client.</summary>
public sealed class WebSocketMessageMock
{
    /// <summary>
    /// The message payload. For <see cref="WebSocketMessageType.Text"/> it is sent
    /// verbatim; for <see cref="WebSocketMessageType.Binary"/> it is base64-decoded first.
    /// </summary>
    public string? Body { get; set; }

    /// <summary>Whether <see cref="Body"/> is text or base64 binary. Defaults to text.</summary>
    public WebSocketMessageType MessageType { get; set; } = WebSocketMessageType.Text;
}

/// <summary>
/// Matches an inbound client message. A <c>null</c> match — or a match with no criteria
/// set — is a catch-all that matches any message. Mirrors the <c>bodyFragment</c> /
/// <c>bodyRegex</c> vocabulary of the HTTP and stdio mock plugins, adding a structural
/// <c>bodyJson</c> for JSON payloads.
/// </summary>
public sealed class WebSocketMessageMatch
{
    /// <summary>A case-insensitive substring matched against the inbound message text.</summary>
    public string? BodyFragment { get; set; }

    /// <summary>A regular expression matched against the inbound message text.</summary>
    public string? BodyRegex { get; set; }

    /// <summary>
    /// A JSON value compared structurally (key-order-independent) against the inbound
    /// message parsed as JSON.
    /// </summary>
    public JsonNode? BodyJson { get; set; }
}

/// <summary>
/// A reactive rule: when an inbound client message satisfies <see cref="Match"/>, the
/// mock server sends <see cref="Responses"/> back, optionally closing afterwards.
/// </summary>
public sealed class WebSocketMessageRule
{
    /// <summary>The matcher for inbound client messages. Null matches any message.</summary>
    public WebSocketMessageMatch? Match { get; set; }

    /// <summary>Messages to send to the client when this rule matches.</summary>
    public IEnumerable<WebSocketMessageMock> Responses { get; set; } = [];

    /// <summary>When true, the mock server closes the connection after replying.</summary>
    public bool CloseAfter { get; set; }
}

/// <summary>
/// A WebSocket mock keyed on a URL: scripted <see cref="OnConnect"/> messages sent
/// immediately after the handshake, plus reactive <see cref="Rules"/> applied to inbound
/// client messages.
/// </summary>
public sealed class WebSocketMock : ICloneable
{
    /// <summary>
    /// The WebSocket URL to match (<c>ws://</c>/<c>wss://</c> or <c>http(s)://</c>;
    /// schemes are normalized when matching). Supports <c>*</c> wildcards.
    /// </summary>
    public string? Url { get; set; }

    /// <summary>Messages sent to the client immediately after the handshake, in order.</summary>
    public IEnumerable<WebSocketMessageMock> OnConnect { get; set; } = [];

    /// <summary>Reactive rules evaluated (in order) against each inbound client message.</summary>
    public IEnumerable<WebSocketMessageRule> Rules { get; set; } = [];

    /// <summary>When true, the mock server closes if an inbound message matches no rule.</summary>
    public bool CloseOnUnmatched { get; set; }

    public object Clone()
    {
        var json = JsonSerializer.Serialize(this);
        return JsonSerializer.Deserialize<WebSocketMock>(json) ?? new WebSocketMock();
    }
}

/// <summary>
/// Pure matching logic for <see cref="WebSocketMessageMatch"/>, factored out so the
/// three match operators are unit-testable without a live socket.
///
/// <code>
///   inbound text ─┬─ bodyJson     → JsonNode.DeepEquals       (highest precedence)
///                 ├─ bodyRegex    → Regex.IsMatch (1s timeout)
///                 └─ bodyFragment → OrdinalIgnoreCase substring
///   (no criteria set, or null match) → catch-all (matches any)
/// </code>
/// </summary>
internal static class WebSocketMessageMatcher
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(1);

    public static bool Matches(WebSocketMessageMatch? match, string message)
    {
        // A null match is a catch-all.
        if (match is null)
        {
            return true;
        }

        // Precedence mirrors the stdio plugin (regex wins over fragment); bodyJson is the
        // most specific, so it takes top precedence. Only the highest-precedence criterion
        // that is set is evaluated.
        if (match.BodyJson is { } expectedJson)
        {
            return JsonEquals(message, expectedJson);
        }

        if (match.BodyRegex is { } pattern)
        {
            return SafeRegexMatch(message, pattern);
        }

        if (match.BodyFragment is { } fragment)
        {
            return message.Contains(fragment, StringComparison.OrdinalIgnoreCase);
        }

        // No criteria set — catch-all.
        return true;
    }

    private static bool SafeRegexMatch(string message, string pattern)
    {
        try
        {
            return Regex.IsMatch(message, pattern, RegexOptions.None, RegexTimeout);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
        catch (ArgumentException)
        {
            // Malformed pattern — treat as no match rather than throwing into the pump.
            return false;
        }
    }

    private static bool JsonEquals(string message, JsonNode expected)
    {
        try
        {
            var actual = JsonNode.Parse(message);
            return JsonNode.DeepEquals(actual, expected);
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
