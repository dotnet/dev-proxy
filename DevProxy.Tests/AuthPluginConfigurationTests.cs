// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Plugins.Mocking;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace DevProxy.Tests;

public class AuthPluginConfigurationTests
{
    [Theory]
    [InlineData("oauth2")]
    [InlineData("oAuth2")]
    public void Get_OAuth2Section_BindsOAuth2(string key)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["type"] = "oauth2",
                [$"{key}:issuer"] = "https://login.example.com",
                [$"{key}:validateLifetime"] = "false",
            })
            .Build();

        var authConfiguration = configuration.Get<AuthPluginConfiguration>();

        Assert.NotNull(authConfiguration?.OAuth2);
        Assert.Equal(AuthPluginAuthType.OAuth2, authConfiguration.Type);
        Assert.Equal("https://login.example.com", authConfiguration.OAuth2.Issuer);
        Assert.False(authConfiguration.OAuth2.ValidateLifetime);
    }
}
