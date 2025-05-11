// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

namespace DevProxy.Plugins;

internal sealed class PluginReference
{
    public bool Enabled { get; set; } = true;
    public string? ConfigSection { get; set; }
    public string Name { get; set; } = string.Empty;
    public string PluginPath { get; set; } = string.Empty;
    public List<string>? UrlsToWatch { get; set; }
}