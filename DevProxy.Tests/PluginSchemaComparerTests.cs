// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Text.Json.Nodes;
using Xunit;

namespace DevProxy.Tests;

public class PluginSchemaComparerTests
{
    public enum SampleMode { Fast, Slow }

    public sealed class SampleConfiguration
    {
        public int MaxMs { get; set; } = 5000;
        public SampleMode Mode { get; set; } = SampleMode.Fast;
        public string? Name { get; set; }
        public List<string> Tags { get; } = [];
    }

    public sealed class RenamedConfiguration
    {
        public int MaximumMs { get; set; } = 5000;
        public SampleMode Mode { get; set; } = SampleMode.Fast;
        public string? Name { get; set; }
        public List<string> Tags { get; } = [];
    }

    public sealed class ChangedDefaultConfiguration
    {
        public int MaxMs { get; set; } = 6000;
        public SampleMode Mode { get; set; } = SampleMode.Fast;
        public string? Name { get; set; }
        public List<string> Tags { get; } = [];
    }

    private static JsonObject Schema() => JsonNode.Parse("""
        {
          "type": "object",
          "properties": {
            "$schema": { "type": "string" },
            "maxMs": { "type": "integer", "default": 5000 },
            "mode": { "type": "string", "default": "fast", "enum": ["Fast", "Slow"] },
            "name": { "type": "string" },
            "tags": { "type": "array", "items": { "type": "string" } }
          }
        }
        """)!.AsObject();

    [Fact]
    public void Compare_MatchingSchema_ReportsNothing()
    {
        Assert.Empty(PluginSchemaComparer.Compare("SamplePlugin", typeof(SampleConfiguration), Schema()));
    }

    [Fact]
    public void Compare_RenamedProperty_ReportsBothSides()
    {
        var mismatches = PluginSchemaComparer.Compare("SamplePlugin", typeof(RenamedConfiguration), Schema());

        Assert.Contains(mismatches, m => m is { Plugin: "SamplePlugin", Property: "maximumMs", Kind: SchemaMismatchKind.MissingInSchema });
        Assert.Contains(mismatches, m => m is { Plugin: "SamplePlugin", Property: "maxMs", Kind: SchemaMismatchKind.MissingInClass });
    }

    [Fact]
    public void Compare_ChangedDefault_ReportsDefault()
    {
        var mismatch = Assert.Single(PluginSchemaComparer.Compare("SamplePlugin", typeof(ChangedDefaultConfiguration), Schema()));

        Assert.Equal(("SamplePlugin", "maxMs", SchemaMismatchKind.Default), (mismatch.Plugin, mismatch.Property, mismatch.Kind));
        Assert.Contains("6000", mismatch.Details, StringComparison.Ordinal);
    }

    [Fact]
    public void Compare_RemovedSchemaProperty_ReportsMissingInSchema()
    {
        var schema = Schema();
        _ = schema["properties"]!.AsObject().Remove("tags");

        var mismatch = Assert.Single(PluginSchemaComparer.Compare("SamplePlugin", typeof(SampleConfiguration), schema));

        Assert.Equal(("SamplePlugin", "tags", SchemaMismatchKind.MissingInSchema), (mismatch.Plugin, mismatch.Property, mismatch.Kind));
    }

    [Fact]
    public void Compare_NonTrivialDefaultNotInSchema_ReportsMissingDefault()
    {
        var schema = Schema();
        _ = schema["properties"]!["maxMs"]!.AsObject().Remove("default");

        var mismatch = Assert.Single(PluginSchemaComparer.Compare("SamplePlugin", typeof(SampleConfiguration), schema));

        Assert.Equal(("maxMs", SchemaMismatchKind.MissingDefault), (mismatch.Property, mismatch.Kind));
    }

    [Fact]
    public void Compare_WrongType_ReportsType()
    {
        var schema = Schema();
        schema["properties"]!["maxMs"] = JsonNode.Parse("""{ "type": "string", "default": 5000 }""");

        var mismatch = Assert.Single(PluginSchemaComparer.Compare("SamplePlugin", typeof(SampleConfiguration), schema));

        Assert.Equal(("maxMs", SchemaMismatchKind.Type), (mismatch.Property, mismatch.Kind));
    }

    [Fact]
    public void Compare_UnionWithIncompatibleType_ReportsType()
    {
        var schema = Schema();
        schema["properties"]!["maxMs"] = JsonNode.Parse("""{ "type": ["integer", "boolean"], "default": 5000 }""");

        var mismatch = Assert.Single(PluginSchemaComparer.Compare("SamplePlugin", typeof(SampleConfiguration), schema));

        Assert.Equal(("maxMs", SchemaMismatchKind.Type), (mismatch.Property, mismatch.Kind));
    }

    [Fact]
    public void Compare_ArrayItemsInsideAnyOf_ComparesItems()
    {
        var schema = Schema();
        schema["properties"]!["tags"] = JsonNode.Parse("""{ "anyOf": [{ "type": "array", "items": { "type": "integer" } }] }""");

        var mismatch = Assert.Single(PluginSchemaComparer.Compare("SamplePlugin", typeof(SampleConfiguration), schema));

        Assert.Equal(("tags[]", SchemaMismatchKind.Type), (mismatch.Property, mismatch.Kind));
    }

    [Theory]
    [InlineData("""{ "default": 5000 }""")]
    [InlineData("""{ "type": "null" }""")]
    [InlineData("""{ "anyOf": [{ "type": "integer" }, {}], "default": 5000 }""")]
    public void Compare_UnconstrainedType_ReportsType(string maxMsSchema)
    {
        var schema = Schema();
        schema["properties"]!["maxMs"] = JsonNode.Parse(maxMsSchema);

        var mismatches = PluginSchemaComparer.Compare("SamplePlugin", typeof(SampleConfiguration), schema);

        Assert.Contains(mismatches, m => m is { Property: "maxMs", Kind: SchemaMismatchKind.Type });
    }

    [Theory]
    [InlineData("""{ "type": "array" }""")]
    [InlineData("""{ "type": "array", "items": true }""")]
    [InlineData("""{ "anyOf": [{ "type": "array", "items": { "type": "string" } }, { "type": "array" }] }""")]
    public void Compare_ArrayWithoutItemConstraints_ReportsItemType(string tagsSchema)
    {
        var schema = Schema();
        schema["properties"]!["tags"] = JsonNode.Parse(tagsSchema);

        var mismatch = Assert.Single(PluginSchemaComparer.Compare("SamplePlugin", typeof(SampleConfiguration), schema));

        Assert.Equal(("tags[]", SchemaMismatchKind.Type), (mismatch.Property, mismatch.Kind));
    }

    [Fact]
    public void Compare_EnumWithoutType_ComparesEnumValues()
    {
        var schema = Schema();
        schema["properties"]!["mode"] = JsonNode.Parse("""{ "default": "fast", "enum": ["Fast", "Medium"] }""");

        var mismatches = PluginSchemaComparer.Compare("SamplePlugin", typeof(SampleConfiguration), schema);

        Assert.Equal(2, mismatches.Count(m => m is { Property: "mode", Kind: SchemaMismatchKind.Enum }));
    }

    [Fact]
    public void Compare_EnumValuesDiffer_ReportsEnum()
    {
        var schema = Schema();
        schema["properties"]!["mode"]!["enum"] = new JsonArray("Fast", "Medium");

        var mismatches = PluginSchemaComparer.Compare("SamplePlugin", typeof(SampleConfiguration), schema);

        Assert.Equal(2, mismatches.Count(m => m is { Property: "mode", Kind: SchemaMismatchKind.Enum }));
    }

    [Fact]
    public void Compare_RequiredButNullable_ReportsRequired()
    {
        var schema = Schema();
        schema["required"] = new JsonArray("name", "maxMs");

        var mismatch = Assert.Single(PluginSchemaComparer.Compare("SamplePlugin", typeof(SampleConfiguration), schema));

        Assert.Equal(("name", SchemaMismatchKind.Required), (mismatch.Property, mismatch.Kind));
    }

    [Fact]
    public void Compare_RequiredButNotInClass_ReportsRequired()
    {
        var schema = Schema();
        schema["required"] = new JsonArray("maxMs", "timeout");

        var mismatch = Assert.Single(PluginSchemaComparer.Compare("SamplePlugin", typeof(SampleConfiguration), schema));

        Assert.Equal(("timeout", SchemaMismatchKind.Required), (mismatch.Property, mismatch.Kind));
    }

    [Fact]
    public void Compare_ItemsWithoutType_ReportsType()
    {
        var schema = Schema();
        schema["properties"]!["tags"] = JsonNode.Parse("""{ "items": { "type": "string" } }""");

        var mismatches = PluginSchemaComparer.Compare("SamplePlugin", typeof(SampleConfiguration), schema);

        Assert.Contains(mismatches, m => m is { Property: "tags", Kind: SchemaMismatchKind.Type });
    }
}
