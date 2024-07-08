// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace Microsoft.DevProxy.Plugins;

public class GenericErrorResponse
{
    public GenericErrorResponseRequest? Request { get; set; }
    public GenericErrorResponseResponse[]? Responses { get; set; }
}

public class GenericErrorResponseRequest
{
    public string Url { get; set; } = string.Empty;
    public string Method { get; set; } = "GET";
    public string? BodyFragment { get; set; }
}

public class GenericErrorResponseResponse
{
    public int? StatusCode { get; set; } = 400;
    public dynamic? Body { get; set; }
    public List<GenericErrorResponseHeader>? Headers { get; set; }
}

public class GenericErrorResponseHeader
{
    public string Name { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;

    public GenericErrorResponseHeader()
    {
    }

    public GenericErrorResponseHeader(string name, string value)
    {
        Name = name;
        Value = value;
    }
}