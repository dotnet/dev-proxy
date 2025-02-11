// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

namespace Microsoft.DevProxy.Abstractions;

public class GraphBatchRequestPayload
{
    public GraphBatchRequestPayloadRequest[] Requests { get; set; } = [];
}

public class GraphBatchRequestPayloadRequest
{
    public string Id { get; set; } = string.Empty;
    public string Method { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public Dictionary<string, string>? Headers { get; set; } = [];
    public object? Body { get; set; }
}