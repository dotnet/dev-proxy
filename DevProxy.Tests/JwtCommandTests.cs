// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Commands;
using DevProxy.Jwt;
using System.CommandLine;
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

        var before = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var token = new JwtSecurityTokenHandler().ReadJwtToken(JwtTokenGenerator.CreateToken(options));
        var after = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        Assert.Equal(["https://myserver.com"], token.Audiences);
        Assert.InRange(new DateTimeOffset(token.ValidFrom).ToUnixTimeSeconds(), before, after);
        Assert.InRange(new DateTimeOffset(token.ValidTo).ToUnixTimeSeconds(), before + 3600, after + 3600);
    }

    private static Dictionary<string, string>? ParseClaims(params string[] claimArgs)
    {
        var jwtCommand = new JwtCommand();
        var createCommand = jwtCommand.Subcommands.Single(c => c.Name == "create");
        var claimsOption = (Option<Dictionary<string, string>>)createCommand.Options.Single(o => o.Name == "--claims");

        var args = new List<string> { "create" };
        foreach (var claim in claimArgs)
        {
            args.Add("--claims");
            args.Add(claim);
        }

        var parseResult = jwtCommand.Parse(args);
        Assert.Empty(parseResult.Errors);
        return parseResult.GetValue(claimsOption);
    }

    [Fact]
    public void Claims_SimpleValue_IsParsed()
    {
        var claims = ParseClaims("dept:eng");

        Assert.NotNull(claims);
        Assert.Equal("eng", claims["dept"]);
    }

    [Fact]
    public void Claims_ValueContainingColon_SplitsOnFirstColonOnly()
    {
        var claims = ParseClaims("iss2:https://example.com", "time:12:30:00");

        Assert.NotNull(claims);
        Assert.Equal("https://example.com", claims["iss2"]);
        Assert.Equal("12:30:00", claims["time"]);
    }

    [Fact]
    public void Claims_MissingSeparator_ReportsError()
    {
        var jwtCommand = new JwtCommand();

        var parseResult = jwtCommand.Parse(["create", "--claims", "dept"]);

        Assert.Contains(parseResult.Errors, e => e.Message.Contains("Invalid claim format", StringComparison.Ordinal));
    }
}
