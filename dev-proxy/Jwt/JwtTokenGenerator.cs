// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.IdentityModel.Tokens.Jwt;
using System.Text;

namespace Microsoft.DevProxy.Jwt;

internal static class JwtTokenGenerator
{
    internal static string CreateToken(JwtOptions jwtOptions)
    {
        var options = JwtCreatorOptions.Create(jwtOptions);

        var jwtIssuer = new JwtIssuer(
            options.Issuer,
            Encoding.UTF8.GetBytes(options.SigningKey)
        );

        var jwtToken = jwtIssuer.CreateSecurityToken(options);
        return new JwtSecurityTokenHandler().WriteToken(jwtToken);
    }
}