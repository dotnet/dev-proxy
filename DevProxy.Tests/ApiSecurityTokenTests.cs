// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Commands;
using Xunit;

namespace DevProxy.Tests;

public class ApiSecurityTokenTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void TryResolveToken_NoOverride_GeneratesRandomToken(string? value)
    {
        Assert.True(ApiSecurity.TryResolveToken(value, out var first, out var error));
        Assert.Null(error);
        Assert.Equal(64, first.Length);
        Assert.True(first.All(Uri.IsHexDigit));

        Assert.True(ApiSecurity.TryResolveToken(value, out var second, out _));
        Assert.NotEqual(first, second);
    }

    [Fact]
    public void TryResolveToken_ValidOverride_UsesValue()
    {
        var value = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));

        Assert.True(ApiSecurity.TryResolveToken(value, out var token, out var error));
        Assert.Null(error);
        Assert.Equal(value, token);
    }

    [Fact]
    public void TryResolveToken_OverrideWithSurroundingWhitespace_IsTrimmed()
    {
        var value = new string('a', 32);

        Assert.True(ApiSecurity.TryResolveToken($"  {value}\n", out var token, out _));
        Assert.Equal(value, token);
    }

    [Theory]
    [InlineData("abcdefghijklmnopqrstuvwxyz-._~+/0123==")]
    [InlineData("ABCDEFGHIJKLMNOPQRSTUVWXYZ012345")]
    public void TryResolveToken_Base64TokenCharacters_Accepted(string value)
    {
        Assert.True(ApiSecurity.TryResolveToken(value, out var token, out _));
        Assert.Equal(value, token);
    }

    [Theory]
    [InlineData("short")]
    [InlineData("0123456789abcdef0123456789abcde")]
    public void TryResolveToken_TooShort_Fails(string value)
    {
        Assert.False(ApiSecurity.TryResolveToken(value, out _, out var error));
        Assert.Contains(ApiSecurity.TokenEnvironmentVariable, error, StringComparison.Ordinal);
        Assert.Contains("32", error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("0123456789abcdef 0123456789abcdef")]
    [InlineData("0123456789abcdef0123456789abcdef\u00e9")]
    [InlineData("0123456789abcdef0123456789abcdef;")]
    [InlineData("0123456789abcdef=0123456789abcdef")]
    public void TryResolveToken_InvalidCharacters_Fails(string value)
    {
        Assert.False(ApiSecurity.TryResolveToken(value, out _, out var error));
        Assert.Contains(ApiSecurity.TokenEnvironmentVariable, error, StringComparison.Ordinal);
    }

    private static readonly string ValidToken = new('a', 64);

    [Fact]
    public void GetEffectiveTokenValue_EnvOptionOverride_WinsOverInherited()
    {
        var options = new DevProxyConfigOptions();
        options.ParseOptions(["--detach", "--env", $"{ApiSecurity.TokenEnvironmentVariable}={ValidToken}"]);

        var value = ApiSecurity.GetEffectiveTokenValue(options.Env, "short");

        Assert.Equal(ValidToken, value);
        Assert.True(ApiSecurity.TryResolveToken(value, out var token, out _));
        Assert.Equal(ValidToken, token);
    }

    [Fact]
    public void GetEffectiveTokenValue_InvalidEnvOptionOverride_IsRejected()
    {
        var options = new DevProxyConfigOptions();
        options.ParseOptions(["--detach", "-e", $"{ApiSecurity.TokenEnvironmentVariable}=short"]);

        var value = ApiSecurity.GetEffectiveTokenValue(options.Env, ValidToken);

        Assert.Equal("short", value);
        Assert.False(ApiSecurity.TryResolveToken(value, out _, out _));
    }

    [Fact]
    public void GetEffectiveTokenValue_NoEnvOptionOverride_UsesInherited()
    {
        var options = new DevProxyConfigOptions();
        options.ParseOptions(["--detach", "--env", "OTHER=value"]);

        Assert.Equal(ValidToken, ApiSecurity.GetEffectiveTokenValue(options.Env, ValidToken));
        Assert.Null(ApiSecurity.GetEffectiveTokenValue(null, null));
    }

    [Theory]
    [InlineData("--detach")]
    [InlineData("-d", "--port", "0", "--api-port", "0")]
    [InlineData("--detach", "--env", "DEV_PROXY_API_TOKEN=short")]
    public void ShouldPreflightApiToken_ProxyStart_ReturnsTrue(params string[] args)
    {
        Assert.True(DevProxyCommand.ShouldPreflightApiToken(args));
    }

    [Theory]
    [InlineData("--detach", "--help")]
    [InlineData("-d", "-h")]
    [InlineData("--detach", "--version")]
    [InlineData("--output", "json", "status", "--detach")]
    [InlineData("--no-color", "stop", "-d")]
    [InlineData("--detach", "api", "token")]
    [InlineData("-d", "stdio", "node", "app.js")]
    public void ShouldPreflightApiToken_HelpVersionOrSubcommand_ReturnsFalse(params string[] args)
    {
        Assert.False(DevProxyCommand.ShouldPreflightApiToken(args));
    }
}
