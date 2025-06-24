// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Jwt;
using System.CommandLine;
using System.CommandLine.Parsing;

namespace DevProxy.Commands;

sealed class JwtCommand : Command
{
    public JwtCommand() :
        base("jwt", "Manage JSON Web Tokens")
    {
        ConfigureCommand();
    }

    private void ConfigureCommand()
    {
        var jwtCreateCommand = new Command("create", "Create a new JWT token");
        var jwtNameOption = new Option<string>("--name", ["-n"])
        {
            Description = "The name of the user to create the token for."
        };

        var jwtAudiencesOption = new Option<IEnumerable<string>>("--audiences", ["-a"])
        {
            Description = "The audiences to create the token for. Specify once for each audience",
            AllowMultipleArgumentsPerToken = true
        };

        var jwtIssuerOption = new Option<string>("--issuer", ["-i"])
        {
            Description = "The issuer of the token."
        };

        var jwtRolesOption = new Option<IEnumerable<string>>("--roles", ["-r"])
        {
            Description = "A role claim to add to the token. Specify once for each role.",
            AllowMultipleArgumentsPerToken = true
        };

        var jwtScopesOption = new Option<IEnumerable<string>>("--scopes", ["-s"])
        {
            Description = "A scope claim to add to the token. Specify once for each scope.",
            AllowMultipleArgumentsPerToken = true
        };

        var jwtClaimsOption = new Option<Dictionary<string, string>>("--claims", ["-c"])
        {
            Description = "Claims to add to the token. Specify once for each claim in the format \"name:value\".",
            AllowMultipleArgumentsPerToken = true
        };
        
        // TODO: Restore custom parsing for claims in beta5

        var jwtValidForOption = new Option<double>("--valid-for", ["-v"])
        {
            Description = "The duration for which the token is valid. Duration is set in minutes."
        };

        var jwtSigningKeyOption = new Option<string>("--signing-key", ["-k"])
        {
            Description = "The signing key to sign the token. Minimum length is 32 characters."
        };
        
        // TODO: Fix validation for beta5
        // jwtSigningKeyOption.Validators.Add(input => { ... });

        jwtCreateCommand.AddOptions(new List<Option>
        {
            jwtNameOption,
            jwtAudiencesOption,
            jwtIssuerOption,
            jwtRolesOption,
            jwtScopesOption,
            jwtClaimsOption,
            jwtValidForOption,
            jwtSigningKeyOption
        }.OrderByName());

        jwtCreateCommand.SetAction((parseResult) => 
        {
            var jwtOptions = new JwtOptions
            {
                Name = parseResult.GetValue(jwtNameOption),
                Audiences = parseResult.GetValue(jwtAudiencesOption),
                Issuer = parseResult.GetValue(jwtIssuerOption),
                Roles = parseResult.GetValue(jwtRolesOption),
                Scopes = parseResult.GetValue(jwtScopesOption),
                Claims = parseResult.GetValue(jwtClaimsOption),
                ValidFor = parseResult.GetValue(jwtValidForOption),
                SigningKey = parseResult.GetValue(jwtSigningKeyOption)
            };
            
            GetToken(jwtOptions);
            return 0;
        });

        this.AddCommands(new List<Command>
        {
            jwtCreateCommand
        }.OrderByName());
    }

    private static void GetToken(JwtOptions jwtOptions)
    {
        var token = JwtTokenGenerator.CreateToken(jwtOptions);

        Console.WriteLine(token);
    }
}