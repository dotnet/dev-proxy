// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Abstractions.Proxy.Http;
using Xunit;

namespace DevProxy.Abstractions.Tests.Proxy.Http;

public class HeaderCollectionTests
{
    [Fact]
    public void Add_PreservesWireOrderAndDuplicates()
    {
        var headers = new HeaderCollection();
        headers.Add("Set-Cookie", "a=1");
        headers.Add("Set-Cookie", "b=2");

        var all = headers.GetAll("set-cookie").ToList();

        Assert.Equal(2, all.Count);
        Assert.Equal("a=1", all[0].Value);
        Assert.Equal("b=2", all[1].Value);
    }

    [Fact]
    public void Contains_And_GetFirst_AreCaseInsensitive()
    {
        var headers = new HeaderCollection();
        headers.Add("Content-Type", "application/json");

        Assert.True(headers.Contains("content-type"));
        Assert.Equal("application/json", headers.GetFirst("CONTENT-TYPE")!.Value);
    }

    [Fact]
    public void GetFirst_ReturnsNull_WhenAbsent()
    {
        var headers = new HeaderCollection();
        Assert.Null(headers.GetFirst("X-Missing"));
        Assert.Empty(headers.GetAll("X-Missing"));
    }

    [Fact]
    public void Replace_CollapsesDuplicatesToSingleValue()
    {
        var headers = new HeaderCollection();
        headers.Add("X-Test", "old1");
        headers.Add("X-Test", "old2");

        headers.Replace("x-test", "new");

        var all = headers.GetAll("X-Test").ToList();
        Assert.Single(all);
        Assert.Equal("new", all[0].Value);
    }

    [Fact]
    public void Replace_AddsWhenAbsent()
    {
        var headers = new HeaderCollection();
        headers.Replace("X-New", "value");
        Assert.Equal("value", headers.GetFirst("X-New")!.Value);
    }

    [Fact]
    public void Remove_RemovesAllOccurrences_AndReportsResult()
    {
        var headers = new HeaderCollection();
        headers.Add("X-Dup", "1");
        headers.Add("X-Dup", "2");

        Assert.True(headers.Remove("x-dup"));
        Assert.Equal(0, headers.Count);
        Assert.False(headers.Remove("x-dup"));
    }

    [Fact]
    public void AddRange_And_Linq_Work()
    {
        var headers = new HeaderCollection(new[]
        {
            new HttpHeader("A", "1"),
            new HttpHeader("B", "2"),
        });
        headers.AddRange(new[] { new HttpHeader("C", "3") });

        Assert.Equal(3, headers.Count);
        Assert.Contains(headers, h => h.Name == "C");
        Assert.Equal("1", headers.First(h => h.Name == "A").Value);
    }

    [Fact]
    public void SeedingConstructor_PreservesOrder()
    {
        var headers = new HeaderCollection(new[]
        {
            new HttpHeader("First", "1"),
            new HttpHeader("Second", "2"),
        });

        var names = headers.Select(h => h.Name).ToList();
        Assert.Equal(new[] { "First", "Second" }, names);
    }
}
