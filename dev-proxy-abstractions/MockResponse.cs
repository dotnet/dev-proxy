// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Text.Json;

namespace DevProxy.Abstractions;

public class MockResponse: ICloneable
{
    public MockResponseRequest? Request { get; set; }
    public MockResponseResponse? Response { get; set; }

    public object Clone()
    {
        var json = JsonSerializer.Serialize(this);
        return JsonSerializer.Deserialize<MockResponse>(json) ?? new MockResponse();
    }
}

public class MockResponseRequest
{
    public string Url { get; set; } = string.Empty;
    public string Method { get; set; } = "GET";
    public int? Nth { get; set; }
    public string? BodyFragment { get; set; }
}

public class MockResponseResponse
{
    public int? StatusCode { get; set; } = 200;
    public dynamic? Body { get; set; }
    public List<MockResponseHeader>? Headers { get; set; }
}

public class MockResponseHeader
{
    public string Name { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;

    public MockResponseHeader()
    {
    }

    public MockResponseHeader(string name, string value)
    {
        Name = name;
        Value = value;
    }
}