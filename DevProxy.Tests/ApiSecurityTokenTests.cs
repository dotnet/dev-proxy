// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

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

    [Theory]
    [InlineData("================================")]
    [InlineData("a===============================")]
    public void TryResolveToken_PaddingNotCountedTowardMinimum_Fails(string value)
    {
        Assert.False(ApiSecurity.TryResolveToken(value, out _, out var error));
        Assert.Contains("padding", error, StringComparison.Ordinal);
    }

    [Fact]
    public void TryResolveToken_MinimumLengthWithPadding_Accepted()
    {
        var value = new string('a', 32) + "==";

        Assert.True(ApiSecurity.TryResolveToken(value, out var token, out _));
        Assert.Equal(value, token);
    }
}
