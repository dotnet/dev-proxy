// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Text.Json;

namespace DevProxy.Abstractions.Models;

/// <summary>
/// Represents a mock response for stdio operations.
/// </summary>
public class MockStdioResponse : ICloneable
{
    /// <summary>
    /// The request pattern to match against stdin.
    /// </summary>
    public MockStdioRequest? Request { get; set; }

    /// <summary>
    /// The mock response to return.
    /// </summary>
    public MockStdioResponseBody? Response { get; set; }

    public object Clone()
    {
        var json = JsonSerializer.Serialize(this);
        return JsonSerializer.Deserialize<MockStdioResponse>(json) ?? new MockStdioResponse();
    }
}

/// <summary>
/// Represents the request pattern for matching stdin.
/// </summary>
public class MockStdioRequest
{
    /// <summary>
    /// A fragment of the stdin body to match.
    /// If null or empty, the mock matches any stdin (or is applied immediately on startup).
    /// </summary>
    public string? BodyFragment { get; set; }

    /// <summary>
    /// The Nth occurrence to match. If null, matches every occurrence.
    /// </summary>
    public int? Nth { get; set; }
}

/// <summary>
/// Represents the mock response body for stdio.
/// </summary>
public class MockStdioResponseBody
{
    /// <summary>
    /// The stdout content to return. Can be a string or a JSON object.
    /// If the value starts with @, it's treated as a file path.
    /// </summary>
    public dynamic? Stdout { get; set; }

    /// <summary>
    /// The stderr content to return. Can be a string or a JSON object.
    /// If the value starts with @, it's treated as a file path.
    /// </summary>
    public dynamic? Stderr { get; set; }
}
