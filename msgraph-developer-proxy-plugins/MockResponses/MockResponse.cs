﻿// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json.Serialization;

namespace Microsoft.Graph.DeveloperProxy.Plugins.MocksResponses;

public class MockResponse {
    [JsonPropertyName("url")]
    public string Url { get; set; } = string.Empty;
    [JsonPropertyName("method")]
    public string Method { get; set; } = "GET";
    [JsonPropertyName("responseCode")]
    public int? ResponseCode { get; set; } = 200;
    [JsonPropertyName("responseBody")]
    public dynamic? ResponseBody { get; set; }
    [JsonPropertyName("responseHeaders")]
    public IDictionary<string, string>? ResponseHeaders { get; set; }
}
