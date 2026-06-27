// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Net;
using System.Text;
using DevProxy.Proxy.Titanium;
using Xunit;
using TitaniumResponse = Titanium.Web.Proxy.Http.Response;

namespace DevProxy.Proxy.Titanium.Tests;

public class TitaniumResponseAdapterTests
{
    [Fact]
    public void StatusCode_IsMappedToHttpStatusCode()
    {
        var response = new TitaniumResponse { StatusCode = 404 };
        var sut = new TitaniumResponseAdapter(response);
        Assert.Equal(HttpStatusCode.NotFound, sut.StatusCode);
    }

    [Fact]
    public void StatusCode_SetterWritesIntToTitanium()
    {
        var response = new TitaniumResponse { StatusCode = 200 };
        var sut = new TitaniumResponseAdapter(response)
        {
            StatusCode = HttpStatusCode.InternalServerError,
        };
        Assert.Equal(500, response.StatusCode);
    }

    [Fact]
    public void StatusDescription_RoundTrips()
    {
        var response = new TitaniumResponse { StatusDescription = "OK" };
        var sut = new TitaniumResponseAdapter(response);
        Assert.Equal("OK", sut.StatusDescription);

        sut.StatusDescription = "Created";
        Assert.Equal("Created", response.StatusDescription);
    }

    [Fact]
    public void StatusDescription_NullSetter_WritesEmptyString()
    {
        var response = new TitaniumResponse { StatusDescription = "OK" };
        var sut = new TitaniumResponseAdapter(response)
        {
            StatusDescription = null,
        };
        Assert.Equal(string.Empty, response.StatusDescription);
    }

    [Fact]
    public void Body_WhenPresent_IsProjectedAsBytes()
    {
        var response = new TitaniumResponse(Encoding.UTF8.GetBytes("hello world"));
        var sut = new TitaniumResponseAdapter(response);
        Assert.True(sut.HasBody);
        Assert.Equal("hello world", Encoding.UTF8.GetString(sut.Body.Span));
    }

    [Fact]
    public void BodyString_WhenPresent_IsProjected()
    {
        var response = new TitaniumResponse(Encoding.UTF8.GetBytes("payload"));
        var sut = new TitaniumResponseAdapter(response);
        Assert.Equal("payload", sut.BodyString);
    }

    [Fact]
    public void BodyAndBodyString_NoBody_ReturnEmpty()
    {
        var response = new TitaniumResponse();
        var sut = new TitaniumResponseAdapter(response);
        Assert.False(sut.HasBody);
        Assert.True(sut.Body.IsEmpty);
        Assert.Equal(string.Empty, sut.BodyString);
    }

    [Fact]
    public void ContentType_IsProjected()
    {
        var response = new TitaniumResponse { ContentType = "application/json" };
        var sut = new TitaniumResponseAdapter(response);
        Assert.Equal("application/json", sut.ContentType);
    }

    [Fact]
    public void Headers_AreProjected()
    {
        var response = new TitaniumResponse();
        response.Headers.AddHeader("X-Trace", "abc");
        var sut = new TitaniumResponseAdapter(response);
        Assert.Equal("abc", sut.Headers.GetFirst("X-Trace")!.Value);
    }

    [Fact]
    public void SetBody_WithSetter_RoutesBytesToSetter()
    {
        byte[]? captured = null;
        var response = new TitaniumResponse(Encoding.UTF8.GetBytes("seed"));
        var sut = new TitaniumResponseAdapter(response, b => captured = b);

        sut.SetBodyString("updated");

        Assert.NotNull(captured);
        Assert.Equal("updated", Encoding.UTF8.GetString(captured!));
    }

    [Fact]
    public void SetBody_WithoutSetter_Throws()
    {
        var response = new TitaniumResponse(Encoding.UTF8.GetBytes("seed"));
        var sut = new TitaniumResponseAdapter(response);
        Assert.Throws<InvalidOperationException>(() => sut.SetBodyString("nope"));
    }
}
