// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Commands;
using DevProxy.Jwt;
using System.IdentityModel.Tokens.Jwt;

using Xunit;

namespace DevProxy.Tests;

public class JwtCommandTests
{
    [Fact]
    public void GetJwtOptions_NoValidForOrAudiences_LeavesThemNull()
    {
        var command = new JwtCommand();

        var options = command.GetJwtOptions(command.Parse("create"));

        Assert.Null(options.ValidFor);
        Assert.Null(options.Audiences);
    }

    [Fact]
    public void GetJwtOptions_ValidForAndAudiences_UsesProvidedValues()
    {
        var command = new JwtCommand();

        var options = command.GetJwtOptions(command.Parse("create --valid-for 30 -a api1 -a api2"));

        Assert.Equal(30, options.ValidFor);
        Assert.Equal(["api1", "api2"], options.Audiences!);
    }

    [Fact]
    public void CreateToken_FromDefaultCliOptions_MatchesApiDefaults()
    {
        var command = new JwtCommand();
        var options = command.GetJwtOptions(command.Parse("create"));

        var token = new JwtSecurityTokenHandler().ReadJwtToken(JwtTokenGenerator.CreateToken(options));

        Assert.Equal(["https://myserver.com"], token.Audiences);
        Assert.Equal(TimeSpan.FromMinutes(60), token.ValidTo - token.ValidFrom);
    }
}
