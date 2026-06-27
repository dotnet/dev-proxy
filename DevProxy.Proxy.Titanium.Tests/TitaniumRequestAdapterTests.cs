// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Text;
using DevProxy.Proxy.Titanium;
using Xunit;
using TitaniumRequest = Titanium.Web.Proxy.Http.Request;

namespace DevProxy.Proxy.Titanium.Tests;

public class TitaniumRequestAdapterTests
{
    private static TitaniumRequest NewRequest() => new()
    {
        Method = "GET",
        HttpVersion = new Version(1, 1),
        RequestUri = new Uri("https://example.com/api/items?id=1"),
    };

    [Fact]
    public void RequestUri_IsProjected()
    {
        var sut = new TitaniumRequestAdapter(NewRequest());
        Assert.Equal(new Uri("https://example.com/api/items?id=1"), sut.RequestUri);
    }

    [Fact]
    public void Url_IsProjected()
    {
        var sut = new TitaniumRequestAdapter(NewRequest());
        Assert.Equal("https://example.com/api/items?id=1", sut.Url);
    }

    [Fact]
    public void Method_IsProjected()
    {
        var request = NewRequest();
        request.Method = "POST";
        var sut = new TitaniumRequestAdapter(request);
        Assert.Equal("POST", sut.Method);
    }

    [Fact]
    public void HttpVersion_IsProjected()
    {
        var sut = new TitaniumRequestAdapter(NewRequest());
        Assert.Equal(new Version(1, 1), sut.HttpVersion);
    }

    [Fact]
    public void IsWebSocketRequest_DefaultsFalse()
    {
        var sut = new TitaniumRequestAdapter(NewRequest());
        Assert.False(sut.IsWebSocketRequest);
    }

    [Fact]
    public void Headers_AreProjected()
    {
        var request = NewRequest();
        request.Headers.AddHeader("X-Custom", "value");
        var sut = new TitaniumRequestAdapter(request);
        Assert.True(sut.Headers.Contains("X-Custom"));
        Assert.Equal("value", sut.Headers.GetFirst("X-Custom")!.Value);
    }

    [Fact]
    public void SetBodyString_WithSetter_RoutesUtf8BytesToSetter()
    {
        byte[]? captured = null;
        var sut = new TitaniumRequestAdapter(NewRequest(), b => captured = b);

        sut.SetBodyString("hello");

        Assert.NotNull(captured);
        Assert.Equal("hello", Encoding.UTF8.GetString(captured!));
    }

    [Fact]
    public void SetBody_WithSetter_RoutesBytesToSetter()
    {
        byte[]? captured = null;
        var sut = new TitaniumRequestAdapter(NewRequest(), b => captured = b);

        sut.SetBody(new byte[] { 1, 2, 3 });

        Assert.Equal(new byte[] { 1, 2, 3 }, captured);
    }

    [Fact]
    public void SetBody_WithoutSetter_Throws()
    {
        var sut = new TitaniumRequestAdapter(NewRequest());
        Assert.Throws<InvalidOperationException>(() => sut.SetBody(new byte[] { 1 }));
    }

    [Fact]
    public void BodyAndBodyString_NoBody_ReturnEmpty()
    {
        var sut = new TitaniumRequestAdapter(NewRequest());
        Assert.False(sut.HasBody);
        Assert.True(sut.Body.IsEmpty);
        Assert.Equal(string.Empty, sut.BodyString);
    }
}
