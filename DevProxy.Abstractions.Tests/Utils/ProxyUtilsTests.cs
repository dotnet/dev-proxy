// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Abstractions.Utils;
using Xunit;

namespace DevProxy.Abstractions.Tests.Utils;

public class ProxyUtilsTests
{
    [Theory]
    [InlineData("wss://ws.example.test/socket", "https://ws.example.test/socket")]
    [InlineData("ws://ws.example.test/socket", "http://ws.example.test/socket")]
    [InlineData("WSS://ws.example.test/*", "https://ws.example.test/*")]
    [InlineData("Ws://ws.example.test/*", "http://ws.example.test/*")]
    public void NormalizeWebSocketScheme_RewritesWebSocketSchemes(string input, string expected) =>
        Assert.Equal(expected, ProxyUtils.NormalizeWebSocketScheme(input));

    [Theory]
    [InlineData("https://api.contoso.com/v1")]
    [InlineData("http://api.contoso.com/v1")]
    [InlineData("api.contoso.com/*")]
    [InlineData("")]
    public void NormalizeWebSocketScheme_LeavesOtherSchemesUntouched(string input) =>
        Assert.Equal(input, ProxyUtils.NormalizeWebSocketScheme(input));

    [Fact]
    public void NormalizeWebSocketScheme_DoesNotRewriteSchemeInPath() =>
        // only a leading ws(s):// is a scheme; an embedded one must survive
        Assert.Equal(
            "https://host/redirect?to=ws://other",
            ProxyUtils.NormalizeWebSocketScheme("https://host/redirect?to=ws://other"));

    [Fact]
    public void NormalizeWebSocketScheme_NullThrows() =>
        Assert.Throws<System.ArgumentNullException>(() => ProxyUtils.NormalizeWebSocketScheme(null!));

    [Fact]
    public void ReplacePathTokens_ExpandsLeadingTildeSlashToHomeDirectory()
    {
        var home = System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile);
        Assert.Equal(System.IO.Path.Join(home, "mocks/mocks.json"), ProxyUtils.ReplacePathTokens("~/mocks/mocks.json"));
    }

    [Fact]
    public void ReplacePathTokens_ExpandsTildeSlashOnlyToHomeDirectory()
    {
        var home = System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile);
        Assert.Equal(home, ProxyUtils.ReplacePathTokens("~/"));
    }

    [Fact]
    public void ReplacePathTokens_ExpandsLeadingTildeBackslashOnWindowsOnly()
    {
        var home = System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile);
        var expected = System.OperatingSystem.IsWindows()
            ? System.IO.Path.Join(home, @"mocks\mocks.json")
            : @"~\mocks\mocks.json";
        Assert.Equal(expected, ProxyUtils.ReplacePathTokens(@"~\mocks\mocks.json"));
    }

    [Theory]
    [InlineData("~mocks/mocks.json")]
    [InlineData("mocks/~/mocks.json")]
    [InlineData("~")]
    public void ReplacePathTokens_DoesNotExpandNonLeadingOrBareTilde(string input) =>
        Assert.Equal(input, ProxyUtils.ReplacePathTokens(input));

    [Fact]
    public void GetFullPath_ResolvesTildeSlashToHomeDirectoryInsteadOfConfigFolder()
    {
        var home = System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile);
        var configFile = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "devproxyrc.json");
        Assert.Equal(
            System.IO.Path.GetFullPath(System.IO.Path.Combine(home, "mocks", "mocks.json")),
            ProxyUtils.GetFullPath("~/mocks/mocks.json", configFile));
    }
}
