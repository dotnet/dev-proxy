// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Text;

namespace DevProxy.Plugins.TypeSpec;

internal enum AuthType
{
    Http,
    ApiKey,
    OAuth2,
    OpenIdConnect,
    NoAuth
}

internal abstract class Auth
{
    public abstract AuthType Type { get; }
}

internal class NoAuth : Auth
{
    public override AuthType Type => AuthType.NoAuth;

    public override string ToString()
    {
        return "NoAuth";
    }
}

internal enum ApiKeyLocation
{
    Query,
    Header,
    Cookie
}

internal class ApiKeyAuth : Auth
{
    public override AuthType Type => AuthType.ApiKey;
    public required string Name { get; init; }
    public required ApiKeyLocation In { get; init; }

    public override string ToString()
    {
        return $"ApiKeyAuth<ApiKeyLocation.{In.ToString().ToLowerInvariant()}, \"{Name}\">";
    }
}

internal class BearerAuth : Auth
{
    public override AuthType Type => AuthType.Http;

    public override string ToString()
    {
        return "BearerAuth";
    }
}

internal enum FlowType
{
    AuthorizationCode,
    Implicit,
    Password,
    ClientCredentials
}

internal class OAuth2Auth : Auth
{
    public string AuthorizationUrl { get; set; } = string.Empty;
    public override AuthType Type => AuthType.OAuth2;
    public required FlowType FlowType { get; init; }
    public required string Name { get; init; }
    public string[] Scopes { get; set; } = [];
    public string RefreshUrl { get; set; } = string.Empty;
    public string TokenUrl { get; set; } = string.Empty;

    override public string ToString()
    {
        return $"{Name}<[{string.Join(", ", Scopes.Select(s => $"\"{s}\""))}]>";
    }

    public string WriteAlias()
    {
        static string i(int size) => new(' ', size);

        var sb = new StringBuilder();
        sb.AppendLine($"alias {Name}<Scopes extends string[]> = OAuth2Auth<");
        sb.AppendLine($"{i(2)}[");
        sb.AppendLine($"{i(4)}{{");
        sb.AppendLine($"{i(6)}type: OAuth2FlowType.{ToFormattedString(FlowType)};");
        sb.AppendLine($"{i(6)}tokenUrl: \"{TokenUrl}\";");
        if (FlowType == FlowType.AuthorizationCode)
        {
            sb.AppendLine($"{i(6)}authorizationUrl: \"{AuthorizationUrl}\";");
        }
        sb.AppendLine($"{i(6)}refreshUrl: \"{RefreshUrl}\";");
        sb.AppendLine($"{i(4)}}}");
        sb.AppendLine($"{i(2)}],");
        sb.AppendLine($"{i(2)}Scopes");
        sb.AppendLine(">;");
        return sb.ToString();
    }

    private static string ToFormattedString(FlowType flowType)
    {
        var s = flowType.ToString();
        return s[0].ToString().ToLowerInvariant() + s[1..];
    }
}