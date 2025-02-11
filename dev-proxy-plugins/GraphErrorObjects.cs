// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Text.Json.Serialization;

namespace Microsoft.DevProxy.Plugins;

public class GraphErrorResponseBody(GraphErrorResponseError error)
{
    public GraphErrorResponseError Error { get; set; } = error;
}

public class GraphErrorResponseError
{
    public string Code { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public GraphErrorResponseInnerError? InnerError { get; set; }
}

public class GraphErrorResponseInnerError
{
    [JsonPropertyName("request-id")]
    public string RequestId { get; set; } = string.Empty;
    public string Date { get; set; } = string.Empty;
}
