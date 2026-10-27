// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Jwt;
using System.CommandLine;
using System.CommandLine.Parsing;

namespace DevProxy.Commands;

sealed class JwtCommand : Command
{
    private Option<string>? _nameOption;
    private Option<IEnumerable<string>>? _audiencesOption;
    private Option<string>? _issuerOption;
    private Option<IEnumerable<string>>? _rolesOption;
    private Option<IEnumerable<string>>? _scopesOption;
    private Option<Dictionary<string, string>>? _claimsOption;
    private Option<double?>? _validForOption;
    private Option<string>? _signingKeyOption;

    public JwtCommand() :
        base("jwt", "Manage JSON Web Tokens")
    {
        ConfigureCommand();
    }

    private void ConfigureCommand()
    {
        var jwtCreateCommand = new Command("create", "Create a new JWT token");
        var jwtNameOption = new Option<string>("--name", "-n")
        {
            Description = "The name of the user to create the token for."
        };

        var jwtAudiencesOption = new Option<IEnumerable<string>>("--audiences", "-a")
        {
            AllowMultipleArgumentsPerToken = true,
            Description = "The audiences to create the token for. Specify once for each audience."
        };

        var jwtIssuerOption = new Option<string>("--issuer", "-i")
        {
            Description = "The issuer of the token."
        };

        var jwtRolesOption = new Option<IEnumerable<string>>("--roles", "-r")
        {
            AllowMultipleArgumentsPerToken = true,
            Description = "A role claim to add to the token. Specify once for each role."
        };

        var jwtScopesOption = new Option<IEnumerable<string>>("--scopes", "-s")
        {
            AllowMultipleArgumentsPerToken = true,
            Description = "A scope claim to add to the token. Specify once for each scope."
        };

        var jwtClaimsOption = new Option<Dictionary<string, string>>("--claims")
        {
            AllowMultipleArgumentsPerToken = true,
            Description = "Claims to add to the token. Specify once for each claim in the format \"name:value\".",
            CustomParser = result =>
            {
                var claims = new Dictionary<string, string>();
                foreach (var token in result.Tokens)
                {
                    var claim = token.Value.Split(':', 2);

                    if (claim.Length != 2)
                    {
                        result.AddError($"Invalid claim format: '{token.Value}'. Expected format is name:value.");
                        return claims ?? [];
                    }

                    try
                    {
                        var (key, value) = (claim[0], claim[1]);
                        claims.Add(key, value);
                    }
                    catch (Exception ex)
                    {
                        result.AddError(ex.Message);
                    }
                }
                return claims;
            }
        };

        var jwtValidForOption = new Option<double?>("--valid-for", "-v")
        {
            Description = "The duration for which the token is valid. Duration is set in minutes."
        };

        var jwtSigningKeyOption = new Option<string>("--signing-key", "-k")
        {
            Description = "The signing key to sign the token. Minimum length is 32 characters."
        };
        jwtSigningKeyOption.Validators.Add(input =>
        {
            try
            {
                var value = input.GetValue(jwtSigningKeyOption);
                if (string.IsNullOrWhiteSpace(value) || value.Length < 32)
                {
                    input.AddError($"Requires option '{jwtSigningKeyOption.Name}' to be at least 32 characters");
                }
            }
            catch (InvalidOperationException ex)
            {
                input.AddError(ex.Message);
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

        HelpExamples.Add(jwtCreateCommand, [
            "devproxy jwt create                                    Create token with defaults",
            "devproxy jwt create -n \"John Doe\" -r admin -r user   Token with name and roles",
            "devproxy jwt create -s \"read\" -s \"write\" -a myapp  Token with scopes and audience",
            "devproxy jwt create --claims \"dept:eng\"              Custom claims",
        ]);

        _nameOption = jwtNameOption;
        _audiencesOption = jwtAudiencesOption;
        _issuerOption = jwtIssuerOption;
        _rolesOption = jwtRolesOption;
        _scopesOption = jwtScopesOption;
        _claimsOption = jwtClaimsOption;
        _validForOption = jwtValidForOption;
        _signingKeyOption = jwtSigningKeyOption;

        jwtCreateCommand.SetAction(parseResult => GetToken(GetJwtOptions(parseResult)));

        this.AddCommands(new List<Command>
        {
            jwtCreateCommand
        }.OrderByName());
    }

    internal JwtOptions GetJwtOptions(ParseResult parseResult)
    {
        var audiences = parseResult.GetValue(_audiencesOption!);

        // Leave omitted options null so that JwtCreatorOptions applies the
        // same defaults as the API (60 minutes, default audience).
        return new JwtOptions
        {
            Name = parseResult.GetValue(_nameOption!),
            Audiences = audiences?.Any() == true ? audiences : null,
            Issuer = parseResult.GetValue(_issuerOption!),
            Roles = parseResult.GetValue(_rolesOption!),
            Scopes = parseResult.GetValue(_scopesOption!),
            Claims = parseResult.GetValue(_claimsOption!),
            ValidFor = parseResult.GetValue(_validForOption!),
            SigningKey = parseResult.GetValue(_signingKeyOption!)
        };
    }

    private static void GetToken(JwtOptions jwtOptions)
    {
        var token = JwtTokenGenerator.CreateToken(jwtOptions);

        Console.WriteLine(token);
    }
}