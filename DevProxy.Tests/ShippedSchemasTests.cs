// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Abstractions.Utils;
using DevProxy.Commands;
using System.Reflection;
using System.Text.RegularExpressions;
using Xunit;

namespace DevProxy.Tests;

public partial class ShippedSchemasTests
{
    [Fact]
    public void ShippedFiles_ReferenceSchemasShippedWithDevProxy()
    {
        var currentVersion = ProxyUtils.NormalizeVersion(
            typeof(DevProxyCommand).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion);
        var appFolder = AppContext.BaseDirectory;
        var schemasFolder = Path.Combine(appFolder, "schemas");

        var schemaUrls = Directory.EnumerateFiles(appFolder, "*.json", SearchOption.AllDirectories)
            .Where(f => !f.StartsWith(schemasFolder, StringComparison.Ordinal))
            .SelectMany(f => DevProxySchemaUrlRegex().Matches(File.ReadAllText(f)).Select(m => m.Value))
            .Where(u => u.Contains($"/v{currentVersion}/", StringComparison.Ordinal))
            .Distinct()
            .ToArray();

        Assert.NotEmpty(schemaUrls);
        Assert.All(schemaUrls, url => Assert.NotNull(ProxyUtils.GetLocalSchemaPath(url, currentVersion, schemasFolder)));
    }

    [GeneratedRegex(@"https://raw\.githubusercontent\.com/dotnet/dev-proxy/main/schemas/v[^/""]+/[^/""]+\.json")]
    private static partial Regex DevProxySchemaUrlRegex();
}
