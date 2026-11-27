// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Reflection;

namespace DevProxy.Abstractions.Utils;

/// <summary>
/// Builds stable documentation shortlinks (https://go.devproxy.net?go=&lt;code&gt;).
/// The shortlink service redirects codes to the actual documentation pages,
/// so the CLI never hard-codes documentation paths.
/// </summary>
public static class DocsLinks
{
    private const string BaseUrl = "https://go.devproxy.net?go=";
    private const string BuiltInPluginsAssemblyName = "DevProxy.Plugins";

    public static string Api { get; } = Get("api");
    public static string Cli { get; } = Get("cli");

    public static string Get(string code)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        return BaseUrl + Uri.EscapeDataString(code.ToLowerInvariant());
    }

    public static bool IsBuiltInPluginAssembly(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        return string.Equals(assembly.GetName().Name, BuiltInPluginsAssemblyName, StringComparison.Ordinal);
    }

    /// <summary>
    /// Returns the documentation link for a plugin, or null if the plugin
    /// isn't shipped with Dev Proxy and has no canonical documentation page.
    /// </summary>
    public static string? ForPlugin(Type pluginType)
    {
        ArgumentNullException.ThrowIfNull(pluginType);
        return IsBuiltInPluginAssembly(pluginType.Assembly) ? Get(pluginType.Name) : null;
    }
}
