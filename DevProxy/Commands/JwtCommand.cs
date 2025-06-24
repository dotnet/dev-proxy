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
        var jwtNameOption = new Option<string>("--name", "The name of the user to create the token for.");
        jwtNameOption.AddAlias("-n");

        var jwtAudiencesOption = new Option<IEnumerable<string>>("--audiences", "The audiences to create the token for. Specify once for each audience")
        {
            AllowMultipleArgumentsPerToken = true
        };
        jwtAudiencesOption.AddAlias("-a");

        var jwtIssuerOption = new Option<string>("--issuer", "The issuer of the token.");
        jwtIssuerOption.AddAlias("-i");

        var jwtRolesOption = new Option<IEnumerable<string>>("--roles", "A role claim to add to the token. Specify once for each role.")
        {
            AllowMultipleArgumentsPerToken = true
        };
        jwtRolesOption.AddAlias("-r");

        var jwtScopesOption = new Option<IEnumerable<string>>("--scopes", "A scope claim to add to the token. Specify once for each scope.")
        {
            AllowMultipleArgumentsPerToken = true
        };
        jwtScopesOption.AddAlias("-s");

        var jwtClaimsOption = new Option<Dictionary<string, string>>("--claims",
            description: "Claims to add to the token. Specify once for each claim in the format \"name:value\".",
            parseArgument: result =>
            {
                var claims = new Dictionary<string, string>();
                foreach (var token in result.Tokens)
                {
                    var claim = token.Value.Split(":");

                    if (claim.Length != 2)
                    {
                        result.ErrorMessage = $"Invalid claim format: '{token.Value}'. Expected format is name:value.";
                        return claims ?? [];
                    }

                    try
                    {
                        var (key, value) = (claim[0], claim[1]);
                        claims.Add(key, value);
                    }
                    catch (Exception ex)
                    {
                        result.ErrorMessage = ex.Message;
                    }
                }
                return claims;
            }
        )
        {
            AllowMultipleArgumentsPerToken = true,
        };

        var jwtValidForOption = new Option<double>("--valid-for", "The duration for which the token is valid. Duration is set in minutes.");
        jwtValidForOption.AddAlias("-v");

        var jwtSigningKeyOption = new Option<string>("--signing-key", "The signing key to sign the token. Minimum length is 32 characters.");
        jwtSigningKeyOption.AddAlias("-k");
        jwtSigningKeyOption.AddValidator(input =>
        {
            try
            {
                var value = input.GetValueForOption(jwtSigningKeyOption);
                if (string.IsNullOrWhiteSpace(value) || value.Length < 32)
                {
                    input.ErrorMessage = $"Requires option '--{jwtSigningKeyOption.Name}' to be at least 32 characters";
                }
            }
            catch (InvalidOperationException ex)
            {
                input.ErrorMessage = ex.Message;
            }
        });

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