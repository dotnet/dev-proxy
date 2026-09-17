// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Text;
using Microsoft.Extensions.Configuration;

namespace DevProxy.Integration.Tests;

/// <summary>
/// Builds the <see cref="IConfigurationSection"/> that configured plugins
/// (<c>BasePlugin&lt;TConfiguration&gt;</c>) bind their <c>Configuration</c> from —
/// from an inline JSON object, so tests never touch disk.
///
/// <para>The plugin's config object is nested under a stable key and the matching
/// section is returned, exactly how the host resolves a plugin's
/// <c>configSection</c> from <c>devproxyrc.json</c>.</para>
/// </summary>
internal static class PluginConfig
{
    private const string SectionName = "plugin";

    /// <summary>
    /// Returns a populated section from a JSON object literal, e.g.
    /// <c>FromJson("{ \"rate\": 100 }")</c>.
    /// </summary>
    public static IConfigurationSection FromJson(string configObjectJson)
    {
        var document = $"{{ \"{SectionName}\": {configObjectJson} }}";
        var configuration = new ConfigurationBuilder()
            .AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(document)))
            .Build();
        return configuration.GetSection(SectionName);
    }

    /// <summary>An absent section, so the plugin falls back to config defaults.</summary>
    public static IConfigurationSection Empty() =>
        new ConfigurationBuilder().Build().GetSection(SectionName);
}
