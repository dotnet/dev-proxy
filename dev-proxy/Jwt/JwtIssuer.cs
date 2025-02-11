// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Microsoft.IdentityModel.Tokens;
using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Principal;

namespace Microsoft.DevProxy.Jwt;

internal sealed class JwtIssuer(string issuer, byte[] signingKeyMaterial)
{
    private readonly SymmetricSecurityKey _signingKey = new(signingKeyMaterial);

    public string Issuer { get; } = issuer;

    public JwtSecurityToken CreateSecurityToken(JwtCreatorOptions options)
    {
        var identity = new GenericIdentity(options.Name);

        identity.AddClaim(new Claim(JwtRegisteredClaimNames.Sub, options.Name));

        var id = Guid.NewGuid().ToString().GetHashCode().ToString("x", CultureInfo.InvariantCulture);
        identity.AddClaim(new Claim(JwtRegisteredClaimNames.Jti, id));

        if (options.Scopes is { } scopesToAdd)
        {
            identity.AddClaims(scopesToAdd.Select(s => new Claim("scp", s)));
        }

        if (options.Roles is { } rolesToAdd)
        {
            identity.AddClaims(rolesToAdd.Select(r => new Claim("roles", r)));
        }

        if (options.Claims is { Count: > 0 } claimsToAdd)
        {
            // filter out registered claims
            // https://www.rfc-editor.org/rfc/rfc7519#section-4.1            
            claimsToAdd.Remove(JwtRegisteredClaimNames.Iss);
            claimsToAdd.Remove(JwtRegisteredClaimNames.Sub);
            claimsToAdd.Remove(JwtRegisteredClaimNames.Aud);
            claimsToAdd.Remove(JwtRegisteredClaimNames.Exp);
            claimsToAdd.Remove(JwtRegisteredClaimNames.Nbf);
            claimsToAdd.Remove(JwtRegisteredClaimNames.Iat);
            claimsToAdd.Remove(JwtRegisteredClaimNames.Jti);
            claimsToAdd.Remove("scp");
            claimsToAdd.Remove("roles");

            identity.AddClaims(claimsToAdd.Select(kvp => new Claim(kvp.Key, kvp.Value)));
        }

        // Although the JwtPayload supports having multiple audiences registered, the
        // creator methods and constructors don't provide a way of setting multiple
        // audiences. Instead, we have to register an `aud` claim for each audience
        // we want to add so that the multiple audiences are populated correctly.

        if (options.Audiences.ToList() is { Count: > 0 } audiences)
        {
            identity.AddClaims(audiences.Select(aud => new Claim(JwtRegisteredClaimNames.Aud, aud)));
        }

        var handler = new JwtSecurityTokenHandler();
        var jwtSigningCredentials = new SigningCredentials(_signingKey, SecurityAlgorithms.HmacSha256Signature);
        var jwtToken = handler.CreateJwtSecurityToken(Issuer, audience: null, identity, options.NotBefore, options.ExpiresOn, issuedAt: DateTime.UtcNow, jwtSigningCredentials);
        return jwtToken;
    }
}