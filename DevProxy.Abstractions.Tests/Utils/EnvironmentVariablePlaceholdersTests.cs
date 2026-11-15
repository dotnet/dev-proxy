// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Abstractions.Utils;
using System.Text.Json;
using Xunit;

namespace DevProxy.Abstractions.Tests.Utils;

public class EnvironmentVariablePlaceholdersTests
{
    private static readonly Dictionary<string, string> Variables = new()
    {
        ["TENANT_ID"] = "contoso",
        ["CLIENT_ID"] = "1234",
        ["QUOTED"] = "a \"quoted\" \\ value"
    };

    private static string? Lookup(string name) => Variables.TryGetValue(name, out var value) ? value : null;

    [Theory]
    [InlineData("${TENANT_ID}", "contoso")]
    [InlineData("${env:TENANT_ID}", "contoso")]
    [InlineData("https://login/${TENANT_ID}/v2.0", "https://login/contoso/v2.0")]
    [InlineData("${TENANT_ID}${CLIENT_ID}", "contoso1234")]
    [InlineData("$${TENANT_ID}", "${TENANT_ID}")]
    [InlineData("$${env:TENANT_ID}", "${env:TENANT_ID}")]
    [InlineData("no placeholders", "no placeholders")]
    [InlineData("{TENANT_ID} @TENANT_ID", "{TENANT_ID} @TENANT_ID")]
    [InlineData("${not a var}", "${not a var}")]
    [InlineData("$TENANT_ID", "$TENANT_ID")]
    public void Resolve_ReplacesPlaceholders(string input, string expected) =>
        Assert.Equal(expected, EnvironmentVariablePlaceholders.Resolve(input, Lookup));

    [Fact]
    public void Resolve_LeavesUnsetVariablesAndReportsThemOnce()
    {
        var unresolved = new List<string>();

        var result = EnvironmentVariablePlaceholders.Resolve("${MISSING}/${TENANT_ID}/${env:MISSING}", Lookup, unresolved);

        Assert.Equal("${MISSING}/contoso/${env:MISSING}", result);
        Assert.Equal(["MISSING"], unresolved);
    }

    [Fact]
    public void Resolve_DoesNotReportEscapedPlaceholders()
    {
        var unresolved = new List<string>();

        _ = EnvironmentVariablePlaceholders.Resolve("$${MISSING}", Lookup, unresolved);

        Assert.Empty(unresolved);
    }

    [Fact]
    public void ResolveInJson_ResolvesStringValuesOnly()
    {
        var json = """
        {
          // comment
          "${TENANT_ID}": "${TENANT_ID}",
          "nested": { "items": ["${CLIENT_ID}", 1, true, null] },
          "quoted": "${QUOTED}",
        }
        """;

        var result = EnvironmentVariablePlaceholders.ResolveInJson(json, Lookup);

        using var doc = JsonDocument.Parse(result);
        var root = doc.RootElement;
        Assert.Equal("contoso", root.GetProperty("${TENANT_ID}").GetString());
        Assert.Equal("1234", root.GetProperty("nested").GetProperty("items")[0].GetString());
        Assert.Equal(1, root.GetProperty("nested").GetProperty("items")[1].GetInt32());
        Assert.Equal("a \"quoted\" \\ value", root.GetProperty("quoted").GetString());
    }

    [Fact]
    public void ResolveInJson_ReturnsOriginalWhenNoPlaceholders()
    {
        var json = "{ /* keep */ \"a\": \"b\" }";

        Assert.Same(json, EnvironmentVariablePlaceholders.ResolveInJson(json, Lookup));
    }

    [Fact]
    public void ResolveInJson_ResolvesUnicodeEscapedPlaceholders()
    {
        var json = """{ "audience": "\u0024{CLIENT_ID}" }""";

        var result = EnvironmentVariablePlaceholders.ResolveInJson(json, Lookup);

        using var doc = JsonDocument.Parse(result);
        Assert.Equal("1234", doc.RootElement.GetProperty("audience").GetString());
    }

    [Fact]
    public void ResolveInJson_ReturnsOriginalWhenEscapesWithoutPlaceholders()
    {
        var json = """{ "a": "\u00e9" }""";

        Assert.Same(json, EnvironmentVariablePlaceholders.ResolveInJson(json, Lookup));
    }

    [Fact]
    public void ResolveInYaml_ResolvesScalarValuesOnly()
    {
        var yaml = """
        ${TENANT_ID}: "${TENANT_ID}"
        quoted: "${QUOTED}"
        base: &base
          id: ${CLIENT_ID}
        copy: *base
        """;

        var result = EnvironmentVariablePlaceholders.ResolveInYaml(yaml, Lookup);

        var stream = new YamlDotNet.RepresentationModel.YamlStream();
        stream.Load(new StringReader(result));
        var root = (YamlDotNet.RepresentationModel.YamlMappingNode)stream.Documents[0].RootNode;
        Assert.Equal("contoso", root["${TENANT_ID}"].ToString());
        Assert.Equal("a \"quoted\" \\ value", root["quoted"].ToString());
        Assert.Equal("1234", ((YamlDotNet.RepresentationModel.YamlMappingNode)root["copy"])["id"].ToString());
    }

    [Fact]
    public void ResolveInYaml_ReturnsOriginalWhenNoPlaceholders()
    {
        var yaml = "a: b # keep";

        Assert.Same(yaml, EnvironmentVariablePlaceholders.ResolveInYaml(yaml, Lookup));
    }

    [Fact]
    public void Resolve_TransformsOnlyTextOutsidePlaceholders()
    {
        var result = EnvironmentVariablePlaceholders.Resolve(
            "{TENANT_ID} $${TENANT_ID} ${CLIENT_ID} ${MISSING}",
            name => name == "CLIENT_ID" ? "{TENANT_ID}" : Lookup(name),
            null,
            text => text.Replace("{TENANT_ID}", "legacy", StringComparison.Ordinal));

        Assert.Equal("legacy ${TENANT_ID} {TENANT_ID} ${MISSING}", result);
    }

    [Theory]
    [InlineData("${env:ProgramFiles(x86)}", @"C:\Program Files (x86)")]
    [InlineData("${env:FOO-BAR}", "dash")]
    [InlineData("${env:FOO.BAR}", "dot")]
    [InlineData("$${env:ProgramFiles(x86)}", "${env:ProgramFiles(x86)}")]
    [InlineData("${ProgramFiles(x86)}", "${ProgramFiles(x86)}")]
    [InlineData("${FOO-BAR}", "${FOO-BAR}")]
    public void Resolve_EnvPrefixAcceptsAnyNameCharactersExceptBrace(string input, string expected)
    {
        var variables = new Dictionary<string, string>
        {
            ["ProgramFiles(x86)"] = @"C:\Program Files (x86)",
            ["FOO-BAR"] = "dash",
            ["FOO.BAR"] = "dot"
        };
        var unresolved = new List<string>();

        var result = EnvironmentVariablePlaceholders.Resolve(input, n => variables.TryGetValue(n, out var v) ? v : null, unresolved);

        Assert.Equal(expected, result);
        Assert.Empty(unresolved);
    }

    [Fact]
    public void Resolve_ReportsUnsetEnvPrefixedNameWithSpecialCharacters()
    {
        var unresolved = new List<string>();

        var result = EnvironmentVariablePlaceholders.Resolve("${env:MISSING(x86)}", Lookup, unresolved);

        Assert.Equal("${env:MISSING(x86)}", result);
        Assert.Equal(["MISSING(x86)"], unresolved);
    }

    [Fact]
    public void Resolve_TransformsTextAroundEnvPrefixedNamesWithSpecialCharacters()
    {
        var result = EnvironmentVariablePlaceholders.Resolve(
            "{TENANT_ID} ${env:FOO-BAR} $${env:FOO-BAR}",
            name => name == "FOO-BAR" ? "{TENANT_ID}" : null,
            null,
            text => text.Replace("{TENANT_ID}", "legacy", StringComparison.Ordinal));

        Assert.Equal("legacy {TENANT_ID} ${env:FOO-BAR}", result);
    }

    [Fact]
    public void ResolveInYaml_AliasUsedAsKey_IsResolvedWithItsAnchor()
    {
        // intentional: aliases share the anchored node, so a key that aliases a
        // resolved value changes with it
        var yaml = """
        base: &id ${TENANT_ID}
        *id : value
        """;

        var result = EnvironmentVariablePlaceholders.ResolveInYaml(yaml, Lookup);

        var stream = new YamlDotNet.RepresentationModel.YamlStream();
        stream.Load(new StringReader(result));
        var root = (YamlDotNet.RepresentationModel.YamlMappingNode)stream.Documents[0].RootNode;
        Assert.Equal("contoso", root["base"].ToString());
        Assert.Equal("value", root["contoso"].ToString());
    }
}