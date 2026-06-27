// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Abstractions.Proxy.Http;
using Xunit;
using DevProxy.Proxy.Titanium;
using TitaniumHeaders = Titanium.Web.Proxy.Http.HeaderCollection;
using TitaniumHttpHeader = Titanium.Web.Proxy.Models.HttpHeader;

namespace DevProxy.Proxy.Titanium.Tests;

public class TitaniumHeaderCollectionTests
{
    private static TitaniumHeaderCollection Wrap(params (string Name, string Value)[] headers)
    {
        var titanium = new TitaniumHeaders();
        foreach (var (name, value) in headers)
        {
            titanium.AddHeader(name, value);
        }

        return new TitaniumHeaderCollection(titanium);
    }

    [Fact]
    public void Constructor_NullHeaders_Throws() =>
        Assert.Throws<ArgumentNullException>(() => new TitaniumHeaderCollection(null!));

    [Fact]
    public void Count_ReflectsUnderlyingHeaders()
    {
        var sut = Wrap(("Accept", "application/json"), ("Host", "example.com"));
        Assert.Equal(2, sut.Count);
    }

    [Fact]
    public void Count_CountsDuplicateHeadersSeparately()
    {
        var sut = Wrap(("Set-Cookie", "a=1"), ("Set-Cookie", "b=2"));
        Assert.Equal(2, sut.Count);
    }

    [Fact]
    public void Contains_IsCaseInsensitive()
    {
        var sut = Wrap(("Content-Type", "text/plain"));
        Assert.True(sut.Contains("content-type"));
        Assert.True(sut.Contains("CONTENT-TYPE"));
        Assert.False(sut.Contains("X-Missing"));
    }

    [Fact]
    public void GetFirst_ReturnsFirstMatch()
    {
        var sut = Wrap(("Set-Cookie", "a=1"), ("Set-Cookie", "b=2"));
        var header = sut.GetFirst("set-cookie");
        Assert.NotNull(header);
        Assert.Equal("Set-Cookie", header!.Name);
        Assert.Equal("a=1", header.Value);
    }

    [Fact]
    public void GetFirst_MissingHeader_ReturnsNull()
    {
        var sut = Wrap(("Accept", "application/json"));
        Assert.Null(sut.GetFirst("X-Missing"));
    }

    [Fact]
    public void GetAll_ReturnsEveryOccurrenceInOrder()
    {
        var sut = Wrap(("Set-Cookie", "a=1"), ("Set-Cookie", "b=2"), ("Set-Cookie", "c=3"));
        var values = sut.GetAll("Set-Cookie").Select(h => h.Value).ToArray();
        Assert.Equal(["a=1", "b=2", "c=3"], values);
    }

    [Fact]
    public void GetAll_MissingHeader_ReturnsEmpty()
    {
        var sut = Wrap(("Accept", "application/json"));
        Assert.Empty(sut.GetAll("X-Missing"));
    }

    [Fact]
    public void Add_NameValue_AppendsHeader()
    {
        var sut = Wrap();
        sut.Add("X-Custom", "value");
        Assert.Equal("value", sut.GetFirst("X-Custom")!.Value);
    }

    [Fact]
    public void Add_Header_AppendsHeader()
    {
        var sut = Wrap();
        sut.Add(new HttpHeader("X-Custom", "value"));
        Assert.Equal("value", sut.GetFirst("X-Custom")!.Value);
    }

    [Fact]
    public void AddRange_AppendsAllHeaders()
    {
        var sut = Wrap();
        sut.AddRange([new HttpHeader("A", "1"), new HttpHeader("B", "2")]);
        Assert.Equal("1", sut.GetFirst("A")!.Value);
        Assert.Equal("2", sut.GetFirst("B")!.Value);
    }

    [Fact]
    public void Replace_RemovesExistingAndSetsSingleValue()
    {
        var sut = Wrap(("X-Dup", "old1"), ("X-Dup", "old2"));
        sut.Replace("X-Dup", "new");
        var all = sut.GetAll("X-Dup").Select(h => h.Value).ToArray();
        Assert.Equal(["new"], all);
    }

    [Fact]
    public void Replace_MissingHeader_AddsIt()
    {
        var sut = Wrap();
        sut.Replace("X-New", "value");
        Assert.Equal("value", sut.GetFirst("X-New")!.Value);
    }

    [Fact]
    public void Remove_ExistingHeader_ReturnsTrueAndRemoves()
    {
        var sut = Wrap(("X-Custom", "value"));
        Assert.True(sut.Remove("X-Custom"));
        Assert.False(sut.Contains("X-Custom"));
    }

    [Fact]
    public void Remove_MissingHeader_ReturnsFalse()
    {
        var sut = Wrap();
        Assert.False(sut.Remove("X-Missing"));
    }

    [Fact]
    public void Enumeration_YieldsAllHeadersIncludingDuplicates()
    {
        var sut = Wrap(("Set-Cookie", "a=1"), ("Set-Cookie", "b=2"), ("Host", "example.com"));
        var pairs = sut.Select(h => (h.Name, h.Value)).ToArray();
        Assert.Equal(3, pairs.Length);
        Assert.Contains(("Set-Cookie", "a=1"), pairs);
        Assert.Contains(("Set-Cookie", "b=2"), pairs);
        Assert.Contains(("Host", "example.com"), pairs);
    }

    [Fact]
    public void Mutation_IsVisibleOnUnderlyingTitaniumCollection()
    {
        var titanium = new TitaniumHeaders();
        titanium.AddHeader(new TitaniumHttpHeader("X-Seed", "seed"));
        var sut = new TitaniumHeaderCollection(titanium);

        sut.Add("X-Added", "added");

        Assert.True(titanium.HeaderExists("X-Added"));
        Assert.Equal("added", titanium.GetFirstHeader("X-Added")!.Value);
    }
}
