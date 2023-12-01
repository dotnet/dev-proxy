﻿// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json.Serialization;
using Microsoft.DevProxy.Abstractions;

namespace Microsoft.DevProxy;

public class ProxyConfiguration: IProxyConfiguration {
    [JsonPropertyName("port")]
    public int Port { get; set; } = 8000;
    [JsonPropertyName("ipAddress")]
    public string? IPAddress { get; set; } = "127.0.0.1";
    [JsonPropertyName("labelMode")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public LabelMode LabelMode { get; set; } = LabelMode.Text;
    [JsonPropertyName("record")]
    public bool Record { get; set; } = false;
    [JsonPropertyName("logLevel")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public LogLevel LogLevel { get; set; } = LogLevel.Info;
    public IEnumerable<int> WatchPids { get; set; } = new List<int>();
    public IEnumerable<string> WatchProcessNames { get; set; } = new List<string>();
    [JsonPropertyName("rate")]
    public int Rate { get; set; } = 50;
    public string ConfigFile { get; set; } = "devproxyrc.json";
}

