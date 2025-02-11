// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

namespace Microsoft.DevProxy.Abstractions;

public class GraphBatchResponsePayload
{
    public GraphBatchResponsePayloadResponse[] Responses { get; set; } = [];
}

public class GraphBatchResponsePayloadResponse
{
    public string Id { get; set; } = string.Empty;
    public int Status { get; set; } = 200;
    public dynamic? Body { get; set; }
    public Dictionary<string, string>? Headers { get; set; }
}

public class GraphBatchResponsePayloadResponseBody
{
    public GraphBatchResponsePayloadResponseBodyError? Error { get; set; }
}

public class GraphBatchResponsePayloadResponseBodyError
{
    public string Code { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
}