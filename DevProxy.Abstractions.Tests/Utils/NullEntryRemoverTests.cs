// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Abstractions.Models;
using DevProxy.Abstractions.Utils;
using System.Text.Json;
using Xunit;

namespace DevProxy.Abstractions.Tests.Utils;

public class NullEntryRemoverTests
{
    public sealed class Root
    {
        public IEnumerable<Item> Items { get; set; } = [];
        public Item[]? Array { get; set; }
        public Dictionary<string, Item>? Map { get; set; }
        public IEnumerable<Item?>? NullableItems { get; set; }
        public Item[][]? Nested { get; set; }
        public Dictionary<string, List<Item>>? MapOfLists { get; set; }
        public List<Item?[]>? NestedNullable { get; set; }
    }

    public sealed class Item
    {
        public IEnumerable<string>? Names { get; set; }
        public MockResponseResponse? Response { get; set; }
    }

    [Fact]
    public void RemovesNullEntriesFromNonNullableCollections()
    {
        var root = Deserialize("""
            {
              "items": [ null, { "names": [ "a", null ], "response": { "headers": [ null ] } } ],
              "array": [ null ],
              "map": { "a": null, "b": {} }
            }
            """);

        var removed = NullEntryRemover.RemoveNullEntries(root);

        var item = Assert.Single(root.Items);
        Assert.Equal(["a"], item.Names!);
        Assert.Empty(item.Response!.Headers!);
        Assert.Empty(root.Array!);
        Assert.Equal(["b"], root.Map!.Keys);
        Assert.Equal(
            ["items[0]", "items[1].names[1]", "items[1].response.headers[0]", "array[0]", "map.a"],
            removed);
    }

    [Fact]
    public void RemovesNullEntriesFromNestedCollections()
    {
        var root = Deserialize("""
            {
              "nested": [ [ null, {} ], null ],
              "mapOfLists": { "a": [ null ] },
              "nestedNullable": [ [ null ] ]
            }
            """);

        var removed = NullEntryRemover.RemoveNullEntries(root);

        var inner = Assert.Single(root.Nested!);
        _ = Assert.Single(inner);
        Assert.Empty(root.MapOfLists!["a"]);
        _ = Assert.Single(Assert.Single(root.NestedNullable!));
        Assert.Equal(["nested[0][0]", "nested[1]", "mapOfLists.a[0]"], removed);
    }

    [Fact]
    public void KeepsNullsInNullableCollectionsAndJsonValues()
    {
        var root = Deserialize("""
            {
              "nullableItems": [ null ],
              "items": [ { "response": { "body": [ null ] } } ]
            }
            """);

        var removed = NullEntryRemover.RemoveNullEntries(root);

        Assert.Empty(removed);
        _ = Assert.Single(root.NullableItems!);
        var body = (JsonElement)Assert.Single(root.Items).Response!.Body!;
        Assert.Equal(JsonValueKind.Null, body[0].ValueKind);
    }

    private static Root Deserialize(string json) =>
        JsonSerializer.Deserialize<Root>(json, ProxyUtils.JsonSerializerOptions)!;
}
