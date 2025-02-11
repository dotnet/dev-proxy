// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

namespace Microsoft.DevProxy.Jwt;

public class JwtOptions
{
    public string? Name { get; set; }
    public IEnumerable<string>? Audiences { get; set; }
    public string? Issuer { get; set; }
    public IEnumerable<string>? Roles { get; set; }
    public IEnumerable<string>? Scopes { get; set; }
    public Dictionary<string, string>? Claims { get; set; }
    public double? ValidFor { get; set; }
    public string? SigningKey { get; set; }
}
