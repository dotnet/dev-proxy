// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Globalization;
using System.Text;

namespace DevProxy.Plugins.Models.TypeSpec;

internal enum ApiKeyLocation
{
    Query,
    Header,
    Cookie
}

internal enum AuthType
{
    Http,
    ApiKey,
    OAuth2,
    OpenIdConnect,
    NoAuth
}

internal enum FlowType
{
    AuthorizationCode,
    Implicit,
    Password,
    ClientCredentials
}

internal abstract class Auth
{
    public abstract AuthType Type { get; }
}

internal sealed class NoAuth : Auth
{
    public override AuthType Type => AuthType.NoAuth;

    public override string ToString() => "NoAuth";
}

internal sealed class ApiKeyAuth : Auth
{
    public override AuthType Type => AuthType.ApiKey;
    public required string Name { get; init; }
    public required ApiKeyLocation In { get; init; }

    public override string ToString() =>
        $"ApiKeyAuth<ApiKeyLocation.{In.ToString().ToLowerInvariant()}, \"{Name}\">";
}

internal sealed class BearerAuth : Auth
{
    public override AuthType Type => AuthType.Http;

    public override string ToString() => "BearerAuth";
}

internal sealed class OAuth2Auth : Auth
{
    public override AuthType Type => AuthType.OAuth2;
    public required FlowType FlowType { get; init; }
    public required string Name { get; init; }
    public string AuthorizationUrl { get; set; } = string.Empty;
    public string RefreshUrl { get; set; } = string.Empty;
    public string[] Scopes { get; set; } = [];
    public string TokenUrl { get; set; } = string.Empty;

    public override string ToString() =>
        $"{Name}<[{string.Join(", ", Scopes.Select(s => $"\"{s}\""))}]>";

    public string WriteAlias()
    {
        static string i(int size)
        {
            return new(' ', size);
        }

        var sb = new StringBuilder();
        _ = sb.AppendLine(CultureInfo.InvariantCulture, $"alias {Name}<Scopes extends string[]> = OAuth2Auth<")
            .AppendLine(CultureInfo.InvariantCulture, $"{i(2)}[")
            .AppendLine(CultureInfo.InvariantCulture, $"{i(4)}{{")
            .AppendLine(CultureInfo.InvariantCulture, $"{i(6)}type: OAuth2FlowType.{ToFormattedString(FlowType)};")
            .AppendLine(CultureInfo.InvariantCulture, $"{i(6)}tokenUrl: \"{TokenUrl}\";");
        if (FlowType == FlowType.AuthorizationCode)
        {
            _ = sb.AppendLine(CultureInfo.InvariantCulture, $"{i(6)}authorizationUrl: \"{AuthorizationUrl}\";");
        }
        _ = sb.AppendLine(CultureInfo.InvariantCulture, $"{i(6)}refreshUrl: \"{RefreshUrl}\";")
            .AppendLine(CultureInfo.InvariantCulture, $"{i(4)}}}")
            .AppendLine(CultureInfo.InvariantCulture, $"{i(2)}],")
            .AppendLine(CultureInfo.InvariantCulture, $"{i(2)}Scopes")
            .AppendLine(">;");
        return sb.ToString();
    }

    private static string ToFormattedString(FlowType flowType)
    {
        var s = flowType.ToString();
        return s[0].ToString().ToLowerInvariant() + s[1..];
    }
}