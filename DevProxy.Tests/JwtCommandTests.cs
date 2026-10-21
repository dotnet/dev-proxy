// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Commands;
using System.CommandLine;

using Xunit;

namespace DevProxy.Tests;

public class JwtCommandTests
{
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
