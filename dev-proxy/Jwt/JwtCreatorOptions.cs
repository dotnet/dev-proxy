// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Security.Cryptography;

namespace Microsoft.DevProxy.Jwt;

internal sealed record JwtCreatorOptions
{
    public required string Scheme { get; init; }
    public required string Name { get; init; }
    public required IEnumerable<string> Audiences { get; init; }
    public required string Issuer { get; init; }
    public DateTime NotBefore { get; init; }
    public DateTime ExpiresOn { get; init; }
    public required IEnumerable<string> Roles { get; init; }
    public required IEnumerable<string> Scopes { get; init; }
    public required Dictionary<string, string> Claims { get; init; }
    public required string SigningKey { get; init; }

    public static JwtCreatorOptions Create(JwtOptions options)
    {
        return new JwtCreatorOptions
        {
            Scheme = "Bearer",
            Name = options.Name ?? "Dev Proxy",
            Audiences = options.Audiences ?? ["https://myserver.com"],
            Issuer = options.Issuer ?? "dev-proxy",
            Roles = options.Roles ?? [],
            Scopes = options.Scopes ?? [],
            Claims = options.Claims ?? [],
            NotBefore = DateTime.UtcNow,
            ExpiresOn = DateTime.UtcNow.AddMinutes(options.ValidFor ?? 60),
            SigningKey = (string.IsNullOrEmpty(options.SigningKey) ? RandomNumberGenerator.GetHexString(32) : options.SigningKey)
        };
    }
}