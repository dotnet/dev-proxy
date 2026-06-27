// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Text;
using DevProxy.Proxy.Kestrel.Internal;

using Xunit;

namespace DevProxy.Proxy.Kestrel.Tests;

public class Http1RequestReaderTests
{
    [Fact]
    public void ParseHead_ParsesRequestLineAndHeaders()
    {
        const string headerText =
            "GET /posts/1 HTTP/1.1\r\n" +
            "Host: example.com\r\n" +
            "Accept: application/json";

        var head = Http1RequestReader.ParseHead(headerText);

        Assert.Equal("GET", head.Method);
        Assert.Equal("/posts/1", head.Target);
        Assert.Equal("HTTP/1.1", head.Version);
        Assert.Contains(head.Headers, h => h.Name == "Host" && h.Value == "example.com");
        Assert.Contains(head.Headers, h => h.Name == "Accept" && h.Value == "application/json");
    }

    [Fact]
    public void ParseHead_Throws_OnMalformedRequestLine()
    {
        _ = Assert.Throws<InvalidOperationException>(() => Http1RequestReader.ParseHead("GARBAGE"));
    }

    [Fact]
    public void GetContentLength_ReadsHeaderCaseInsensitively()
    {
        var headers = new List<(string Name, string Value)>
        {
            ("Host", "example.com"),
            ("content-length", "42"),
        };

        Assert.Equal(42, Http1RequestReader.GetContentLength(headers));
    }

    [Fact]
    public void GetContentLength_DefaultsToZero_WhenAbsent()
    {
        var headers = new List<(string Name, string Value)> { ("Host", "example.com") };

        Assert.Equal(0, Http1RequestReader.GetContentLength(headers));
    }

    [Fact]
    public void IndexOfDoubleCrlf_FindsTerminator()
    {
        var data = Encoding.ASCII.GetBytes("ab\r\n\r\ncd");

        Assert.Equal(2, Http1RequestReader.IndexOfDoubleCrlf(data));
    }

    [Fact]
    public void IndexOfDoubleCrlf_ReturnsMinusOne_WhenAbsent()
    {
        var data = Encoding.ASCII.GetBytes("no terminator here");

        Assert.Equal(-1, Http1RequestReader.IndexOfDoubleCrlf(data));
    }
}
