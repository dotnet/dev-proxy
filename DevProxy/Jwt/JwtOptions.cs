// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

namespace DevProxy.Jwt;

#pragma warning disable CA1515 // required for the API controller
public sealed class JwtOptions
#pragma warning restore CA1515
{
    public IEnumerable<string>? Audiences { get; set; }
    public Dictionary<string, string>? Claims { get; init; }
    public string? Issuer { get; set; }
    public string? Name { get; set; }
    public IEnumerable<string>? Roles { get; set; }
    public IEnumerable<string>? Scopes { get; set; }
    public string? SigningKey { get; set; }
    public double? ValidFor { get; set; }
}
