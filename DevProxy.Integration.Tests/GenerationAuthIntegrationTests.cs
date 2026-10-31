// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Security.Claims;
using System.Text.Json.Nodes;
using DevProxy.Abstractions.Models;
using DevProxy.Abstractions.Proxy;
using DevProxy.Abstractions.Utils;
using DevProxy.Plugins.Generation;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DevProxy.Integration.Tests;

/// <summary>
/// Coverage for authentication detection in the API spec generator plugins
/// (OpenAPI security schemes and TypeSpec <c>@useAuth</c>).
/// </summary>
public sealed class GenerationAuthIntegrationTests
{
    private const string EntraIssuerV2 = "https://login.microsoftonline.com/00000000-0000-0000-0000-000000000001/v2.0";
    private const string EntraIssuerV1 = "https://sts.windows.net/00000000-0000-0000-0000-000000000001/";
    private const string EntraBaseUrl = "https://login.microsoftonline.com/00000000-0000-0000-0000-000000000001/";

    private static ISet<UrlToWatch> Watch => KestrelProxyHarness.BuildUrlsToWatch("api.contoso.com");

    private static RecordingArgs Recording(IEnumerable<RequestLog> logs) =>
        new(logs)
        {
            GlobalData = new() { [ProxyUtils.ReportsKey] = new Dictionary<string, object>() },
        };

    private static RequestLog Exchange(string url, params (string Name, string Value)[] headers) =>
        Exchange(url, HttpStatusCode.OK, headers);

    private static RequestLog Exchange(string url, HttpStatusCode statusCode, params (string Name, string Value)[] headers) =>
        TestExchange
            .Request("GET", url, headers: [("Accept", "application/json"), .. headers])
            .WithResponse(
                statusCode,
                headers: [("Content-Type", "application/json")],
                body: """[ { "id": 1, "name": "alpha" } ]""")
            .AsRequestLog(MessageType.InterceptedResponse);

    private static string Jwt(string issuer, string? version = null, string? scp = null, params string[] roles)
    {
        var claims = new List<Claim>();
        if (version is not null)
        {
            claims.Add(new("ver", version));
        }
        if (scp is not null)
        {
            claims.Add(new("scp", scp));
        }
        claims.AddRange(roles.Select(r => new Claim("roles", r)));
        return new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(issuer, claims: claims));
    }

    private static async Task<string> GenerateOpenApiAsync(IEnumerable<RequestLog> logs, IConfigurationSection? config = null)
    {
        var plugin = new OpenApiSpecGeneratorPlugin(
            TestDefaults.HttpClient,
            NullLogger<OpenApiSpecGeneratorPlugin>.Instance,
            Watch,
            new DisabledLanguageModelClient(),
            new TestProxyConfiguration(),
            config ?? PluginConfig.Empty());
        return await GenerateAsync(plugin.AfterRecordingStopAsync, logs, "*.json");
    }

    private static async Task<JsonNode> GenerateOpenApiJsonAsync(IEnumerable<RequestLog> logs, IConfigurationSection? config = null) =>
        JsonNode.Parse(await GenerateOpenApiAsync(logs, config))!;

    private static async Task<string> GenerateTypeSpecAsync(IEnumerable<RequestLog> logs)
    {
        var plugin = new TypeSpecGeneratorPlugin(
            TestDefaults.HttpClient,
            NullLogger<TypeSpecGeneratorPlugin>.Instance,
            Watch,
            new DisabledLanguageModelClient(),
            new TestProxyConfiguration(),
            PluginConfig.Empty());
        return await GenerateAsync(plugin.AfterRecordingStopAsync, logs, "*.tsp");
    }

    private static async Task<string> GenerateAsync(
        Func<RecordingArgs, CancellationToken, Task> generate,
        IEnumerable<RequestLog> logs,
        string pattern)
    {
        var dir = Directory.CreateTempSubdirectory("devproxy-gen-auth-");
        var originalCwd = Directory.GetCurrentDirectory();
        Directory.SetCurrentDirectory(dir.FullName);
        try
        {
            await generate(Recording(logs), CancellationToken.None);
            return await File.ReadAllTextAsync(Assert.Single(dir.GetFiles(pattern)).FullName);
        }
        finally
        {
            Directory.SetCurrentDirectory(originalCwd);
            dir.Delete(recursive: true);
        }
    }

    private static JsonNode Operation(JsonNode doc, string path = "/users") => doc["paths"]![path]!["get"]!;

    private static JsonObject SecurityScheme(JsonNode doc, string name) =>
        doc["components"]!["securitySchemes"]![name]!.AsObject();

    private static string[] RequirementScopes(JsonNode operation, string schemeName) =>
        [.. operation["security"]!.AsArray()
            .Select(r => r![schemeName])
            .Single(s => s is not null)!
            .AsArray()
            .Select(s => s!.GetValue<string>())];

    [Fact]
    public async Task OpenApiSpecGenerator_NoAuth_EmitsNoSecurity()
    {
        var doc = await GenerateOpenApiJsonAsync([Exchange("https://api.contoso.com/users")]);

        Assert.Null(doc["components"]?["securitySchemes"]);
        Assert.Null(Operation(doc)["security"]);
    }

    [Fact]
    public async Task OpenApiSpecGenerator_ApiKeyHeader_EmitsApiKeyScheme()
    {
        var doc = await GenerateOpenApiJsonAsync([Exchange("https://api.contoso.com/users", ("x-api-key", "secret-value"))]);

        var scheme = SecurityScheme(doc, "ApiKeyAuth_header_x-api-key");
        Assert.Equal("apiKey", scheme["type"]!.GetValue<string>());
        Assert.Equal("x-api-key", scheme["name"]!.GetValue<string>());
        Assert.Equal("header", scheme["in"]!.GetValue<string>());
        Assert.Empty(RequirementScopes(Operation(doc), "ApiKeyAuth_header_x-api-key"));
        Assert.DoesNotContain("secret-value", doc.ToJsonString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task OpenApiSpecGenerator_ApiKeyQuery_EmitsApiKeySchemeAndSkipsParameter()
    {
        var config = PluginConfig.FromJson("""{ "includeParameters": ["code"] }""");
        var doc = await GenerateOpenApiJsonAsync([Exchange("https://api.contoso.com/users?code=secret-value&top=10")], config);

        var scheme = SecurityScheme(doc, "ApiKeyAuth_query_code");
        Assert.Equal("apiKey", scheme["type"]!.GetValue<string>());
        Assert.Equal("code", scheme["name"]!.GetValue<string>());
        Assert.Equal("query", scheme["in"]!.GetValue<string>());
        var parameters = Operation(doc)["parameters"]?.AsArray().Select(p => p!["name"]!.GetValue<string>()) ?? [];
        Assert.DoesNotContain("code", parameters);
        Assert.DoesNotContain("secret-value", doc.ToJsonString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task OpenApiSpecGenerator_BasicAuthorization_EmitsApiKeyHeaderScheme()
    {
        var doc = await GenerateOpenApiJsonAsync([Exchange("https://api.contoso.com/users", ("Authorization", "Basic dXNlcjpwYXNz"))]);

        var scheme = SecurityScheme(doc, "ApiKeyAuth_header_Authorization");
        Assert.Equal("apiKey", scheme["type"]!.GetValue<string>());
        Assert.Equal("Authorization", scheme["name"]!.GetValue<string>());
        Assert.Equal("header", scheme["in"]!.GetValue<string>());
    }

    [Fact]
    public async Task OpenApiSpecGenerator_OpaqueBearer_EmitsHttpBearerScheme()
    {
        var doc = await GenerateOpenApiJsonAsync([Exchange("https://api.contoso.com/users", ("Authorization", "Bearer opaque-token"))]);

        var scheme = SecurityScheme(doc, "BearerAuth");
        Assert.Equal("http", scheme["type"]!.GetValue<string>());
        Assert.Equal("bearer", scheme["scheme"]!.GetValue<string>());
        Assert.Empty(RequirementScopes(Operation(doc), "BearerAuth"));
    }

    [Fact]
    public async Task OpenApiSpecGenerator_EntraDelegatedToken_EmitsAuthorizationCodeFlow()
    {
        var token = Jwt(EntraIssuerV2, version: "2.0", scp: "User.Read Mail.Read");
        var doc = await GenerateOpenApiJsonAsync([Exchange("https://api.contoso.com/users", ("Authorization", $"Bearer {token}"))]);

        var scheme = SecurityScheme(doc, "EntraOAuth2Auth");
        Assert.Equal("oauth2", scheme["type"]!.GetValue<string>());
        var flow = scheme["flows"]!["authorizationCode"]!;
        Assert.Equal($"{EntraBaseUrl}oauth2/v2.0/authorize", flow["authorizationUrl"]!.GetValue<string>());
        Assert.Equal($"{EntraBaseUrl}oauth2/v2.0/token", flow["tokenUrl"]!.GetValue<string>());
        Assert.Equal($"{EntraBaseUrl}oauth2/v2.0/token", flow["refreshUrl"]!.GetValue<string>());
        Assert.Equal(["User.Read", "Mail.Read"], flow["scopes"]!.AsObject().Select(s => s.Key));
        Assert.Null(scheme["flows"]!["clientCredentials"]);
        Assert.Equal(["User.Read", "Mail.Read"], RequirementScopes(Operation(doc), "EntraOAuth2Auth"));
    }

    [Fact]
    public async Task OpenApiSpecGenerator_EntraAppToken_EmitsClientCredentialsFlow()
    {
        var token = Jwt(EntraIssuerV1, roles: ["User.Read.All", "Mail.Read"]);
        var doc = await GenerateOpenApiJsonAsync([Exchange("https://api.contoso.com/users", ("Authorization", $"Bearer {token}"))]);

        var scheme = SecurityScheme(doc, "EntraOAuth2Auth");
        var flow = scheme["flows"]!["clientCredentials"]!;
        Assert.Null(flow["authorizationUrl"]);
        Assert.Equal($"{EntraBaseUrl}oauth2/token", flow["tokenUrl"]!.GetValue<string>());
        Assert.Equal(["User.Read.All", "Mail.Read"], flow["scopes"]!.AsObject().Select(s => s.Key));
        Assert.Null(scheme["flows"]!["authorizationCode"]);
        Assert.Equal(["User.Read.All", "Mail.Read"], RequirementScopes(Operation(doc), "EntraOAuth2Auth"));
    }

    [Fact]
    public async Task OpenApiSpecGenerator_EntraTokenWithScopesAndRoles_EmitsAuthorizationCodeFlow()
    {
        var token = Jwt(EntraIssuerV2, version: "2.0", scp: "User.Read", roles: ["Admin"]);
        var doc = await GenerateOpenApiJsonAsync([Exchange("https://api.contoso.com/users", ("Authorization", $"Bearer {token}"))]);

        var flows = SecurityScheme(doc, "EntraOAuth2Auth")["flows"]!;
        Assert.NotNull(flows["authorizationCode"]);
        Assert.Null(flows["clientCredentials"]);
        Assert.Equal(["User.Read", "Admin"], RequirementScopes(Operation(doc), "EntraOAuth2Auth"));
    }

    [Fact]
    public async Task OpenApiSpecGenerator_NonEntraJwt_EmitsHttpBearerScheme()
    {
        var token = Jwt("https://auth.contoso.com/", scp: "read");
        var doc = await GenerateOpenApiJsonAsync([Exchange("https://api.contoso.com/users", ("Authorization", $"Bearer {token}"))]);

        var scheme = SecurityScheme(doc, "BearerAuth");
        Assert.Equal("http", scheme["type"]!.GetValue<string>());
        Assert.Equal("bearer", scheme["scheme"]!.GetValue<string>());
        Assert.Equal("JWT", scheme["bearerFormat"]!.GetValue<string>());
        Assert.Empty(RequirementScopes(Operation(doc), "BearerAuth"));
        Assert.Null(doc["components"]!["securitySchemes"]!["APIOAuth2Auth"]);
    }

    [Fact]
    public async Task OpenApiSpecGenerator_DelegatedAndAppTokens_EmitSeparateRequirements()
    {
        var doc = await GenerateOpenApiJsonAsync([
            Exchange("https://api.contoso.com/users", ("Authorization", $"Bearer {Jwt(EntraIssuerV2, version: "2.0", scp: "User.Read")}")),
            Exchange("https://api.contoso.com/users", ("Authorization", $"Bearer {Jwt(EntraIssuerV2, version: "2.0", roles: ["User.Read.All"])}")),
            Exchange("https://api.contoso.com/users", ("Authorization", $"Bearer {Jwt(EntraIssuerV2, version: "2.0", scp: "User.ReadBasic.All")}")),
        ]);

        var flows = SecurityScheme(doc, "EntraOAuth2Auth")["flows"]!;
        Assert.Equal(["User.Read", "User.ReadBasic.All"], flows["authorizationCode"]!["scopes"]!.AsObject().Select(s => s.Key));
        Assert.Equal(["User.Read.All"], flows["clientCredentials"]!["scopes"]!.AsObject().Select(s => s.Key));
        var security = Operation(doc)["security"]!.AsArray();
        Assert.Equal(2, security.Count);
        Assert.Equal(["User.Read", "User.ReadBasic.All"], security[0]!["EntraOAuth2Auth"]!.AsArray().Select(s => s!.GetValue<string>()));
        Assert.Equal(["User.Read.All"], security[1]!["EntraOAuth2Auth"]!.AsArray().Select(s => s!.GetValue<string>()));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task OpenApiSpecGenerator_SuccessfulAnonymousCall_MakesAuthOptional(bool anonymousFirst)
    {
        var anonymous = Exchange("https://api.contoso.com/users");
        var authenticated = Exchange("https://api.contoso.com/users", ("x-api-key", "secret-value"));
        var doc = await GenerateOpenApiJsonAsync(anonymousFirst ? [anonymous, authenticated] : [authenticated, anonymous]);

        var security = Operation(doc)["security"]!.AsArray();
        Assert.Equal(2, security.Count);
        Assert.NotNull(security[0]!["ApiKeyAuth_header_x-api-key"]);
        Assert.Empty(security[1]!.AsObject());
    }

    [Fact]
    public async Task OpenApiSpecGenerator_SuccessfulCookieCall_KeepsAuthRequired()
    {
        var doc = await GenerateOpenApiJsonAsync([
            Exchange("https://api.contoso.com/users", ("Cookie", "session=abc")),
            Exchange("https://api.contoso.com/users", ("x-api-key", "secret-value")),
        ]);

        var security = Operation(doc)["security"]!.AsArray();
        Assert.NotNull(Assert.Single(security)!["ApiKeyAuth_header_x-api-key"]);
    }

    [Theory]
    [InlineData("access_token")]
    [InlineData("subscription-key")]
    [InlineData("sig")]
    [InlineData("api-key")]
    [InlineData("token")]
    public async Task OpenApiSpecGenerator_AuthQueryParameter_EmitsApiKeySchemeAndSkipsParameter(string name)
    {
        var doc = await GenerateOpenApiJsonAsync([Exchange($"https://api.contoso.com/users?{name}=secret-value&top=10")]);

        var scheme = SecurityScheme(doc, $"ApiKeyAuth_query_{name}");
        Assert.Equal("query", scheme["in"]!.GetValue<string>());
        Assert.Equal(name, scheme["name"]!.GetValue<string>());
        var parameters = Operation(doc)["parameters"]?.AsArray().Select(p => p!["name"]!.GetValue<string>()) ?? [];
        Assert.DoesNotContain(name, parameters);
    }

    [Fact]
    public async Task OpenApiSpecGenerator_QueryAccessTokenAndBearerCalls_KeepAuthRequired()
    {
        var doc = await GenerateOpenApiJsonAsync([
            Exchange("https://api.contoso.com/users?access_token=abc"),
            Exchange("https://api.contoso.com/users", ("Authorization", "Bearer opaque-token")),
        ]);

        var security = Operation(doc)["security"]!.AsArray();
        Assert.Equal(2, security.Count);
        Assert.NotNull(security[0]!["ApiKeyAuth_query_access_token"]);
        Assert.NotNull(security[1]!["BearerAuth"]);
        Assert.DoesNotContain(security, r => r!.AsObject().Count == 0);
    }

    [Fact]
    public async Task OpenApiSpecGenerator_UnderscoreApiKeyQueryParameter_EmitsApiKeyScheme()
    {
        var doc = await GenerateOpenApiJsonAsync([Exchange("https://api.contoso.com/users?api_key=secret-value")]);

        var scheme = SecurityScheme(doc, "ApiKeyAuth_query_api_key");
        Assert.Equal("apiKey", scheme["type"]!.GetValue<string>());
        Assert.Equal("query", scheme["in"]!.GetValue<string>());
        Assert.Equal("api_key", scheme["name"]!.GetValue<string>());
    }

    [Fact]
    public async Task OpenApiSpecGenerator_FailedAnonymousCall_KeepsAuthRequired()
    {
        var doc = await GenerateOpenApiJsonAsync([
            Exchange("https://api.contoso.com/users", HttpStatusCode.Unauthorized),
            Exchange("https://api.contoso.com/users", ("x-api-key", "secret-value")),
        ]);

        var security = Operation(doc)["security"]!.AsArray();
        Assert.NotNull(Assert.Single(security)!["ApiKeyAuth_header_x-api-key"]);
    }

    [Fact]
    public async Task OpenApiSpecGenerator_MergesScopesAcrossRequests()
    {
        var doc = await GenerateOpenApiJsonAsync([
            Exchange("https://api.contoso.com/users", ("Authorization", $"Bearer {Jwt(EntraIssuerV2, version: "2.0", scp: "User.Read")}")),
            Exchange("https://api.contoso.com/users", ("Authorization", $"Bearer {Jwt(EntraIssuerV2, version: "2.0", scp: "User.Read User.ReadBasic.All")}")),
            Exchange("https://api.contoso.com/groups", ("Authorization", $"Bearer {Jwt(EntraIssuerV2, version: "2.0", scp: "Group.Read.All")}")),
        ]);

        var flow = SecurityScheme(doc, "EntraOAuth2Auth")["flows"]!["authorizationCode"]!;
        Assert.Equal(["User.Read", "User.ReadBasic.All", "Group.Read.All"], flow["scopes"]!.AsObject().Select(s => s.Key));
        _ = Assert.Single(Operation(doc)["security"]!.AsArray());
        Assert.Equal(["User.Read", "User.ReadBasic.All"], RequirementScopes(Operation(doc), "EntraOAuth2Auth"));
        Assert.Equal(["Group.Read.All"], RequirementScopes(Operation(doc, "/groups"), "EntraOAuth2Auth"));
    }

    [Fact]
    public async Task OpenApiSpecGenerator_DifferentAuthOnSameOperation_EmitsAlternativeRequirements()
    {
        var doc = await GenerateOpenApiJsonAsync([
            Exchange("https://api.contoso.com/users", ("x-api-key", "secret-value")),
            Exchange("https://api.contoso.com/users", ("Authorization", "Bearer opaque-token")),
        ]);

        var security = Operation(doc)["security"]!.AsArray();
        Assert.Equal(2, security.Count);
        Assert.NotNull(security[0]!["ApiKeyAuth_header_x-api-key"]);
        Assert.NotNull(security[1]!["BearerAuth"]);
    }

    [Fact]
    public async Task OpenApiSpecGenerator_V2_EmitsSecurityDefinitions()
    {
        var token = Jwt(EntraIssuerV2, version: "2.0", scp: "User.Read");
        var doc = await GenerateOpenApiJsonAsync(
            [Exchange("https://api.contoso.com/users", ("Authorization", $"Bearer {token}"))],
            PluginConfig.FromJson("""{ "specVersion": "v2_0" }"""));

        var scheme = doc["securityDefinitions"]!["EntraOAuth2Auth"]!;
        Assert.Equal("oauth2", scheme["type"]!.GetValue<string>());
        Assert.Equal("accessCode", scheme["flow"]!.GetValue<string>());
        Assert.Equal(["User.Read"], RequirementScopes(Operation(doc), "EntraOAuth2Auth"));
    }

    [Fact]
    public async Task OpenApiSpecGenerator_V2_DelegatedAndAppTokens_EmitSeparateSchemes()
    {
        var doc = await GenerateOpenApiJsonAsync(
            [
                Exchange("https://api.contoso.com/users", ("Authorization", $"Bearer {Jwt(EntraIssuerV2, version: "2.0", scp: "User.Read")}")),
                Exchange("https://api.contoso.com/users", ("Authorization", $"Bearer {Jwt(EntraIssuerV2, version: "2.0", roles: ["User.Read.All"])}")),
            ],
            PluginConfig.FromJson("""{ "specVersion": "v2_0" }"""));

        var delegated = doc["securityDefinitions"]!["EntraOAuth2Auth"]!;
        Assert.Equal("accessCode", delegated["flow"]!.GetValue<string>());
        Assert.Equal(["User.Read"], delegated["scopes"]!.AsObject().Select(s => s.Key));
        var app = doc["securityDefinitions"]!["EntraOAuth2Auth_clientCredentials"]!;
        Assert.Equal("application", app["flow"]!.GetValue<string>());
        Assert.Equal(["User.Read.All"], app["scopes"]!.AsObject().Select(s => s.Key));
        Assert.Equal(["User.Read"], RequirementScopes(Operation(doc), "EntraOAuth2Auth"));
        Assert.Equal(["User.Read.All"], RequirementScopes(Operation(doc), "EntraOAuth2Auth_clientCredentials"));
    }

    [Theory]
    [InlineData("https://attacker.example/https://login.microsoftonline.com/tenant/v2.0")]
    [InlineData("http://login.microsoftonline.com/tenant/v2.0")]
    [InlineData("https://login.microsoftonline.com.attacker.example/tenant/v2.0")]
    public async Task Generators_SpoofedEntraIssuer_FallBackToBearer(string issuer)
    {
        var token = Jwt(issuer, version: "2.0", roles: ["User.Read.All"]);
        var logs = new[] { Exchange("https://api.contoso.com/users", ("Authorization", $"Bearer {token}")) };

        var doc = await GenerateOpenApiJsonAsync(logs);
        Assert.Equal("JWT", SecurityScheme(doc, "BearerAuth")["bearerFormat"]!.GetValue<string>());
        Assert.Null(doc["components"]!["securitySchemes"]!["EntraOAuth2Auth"]);

        var tsp = await GenerateTypeSpecAsync(logs);
        Assert.Contains("@useAuth(BearerAuth)", tsp, StringComparison.Ordinal);
        Assert.DoesNotContain("attacker", tsp, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TypeSpecGenerator_EntraDelegatedToken_UsesOAuth2Auth()
    {
        var token = Jwt(EntraIssuerV2, version: "2.0", scp: "User.Read Mail.Read");
        var tsp = await GenerateTypeSpecAsync([Exchange("https://api.contoso.com/users", ("Authorization", $"Bearer {token}"))]);

        Assert.Contains("alias EntraOAuth2Auth<Scopes extends string[]> = OAuth2Auth<", tsp, StringComparison.Ordinal);
        Assert.Contains("type: OAuth2FlowType.authorizationCode;", tsp, StringComparison.Ordinal);
        Assert.Contains($"authorizationUrl: \"{EntraBaseUrl}oauth2/v2.0/authorize\";", tsp, StringComparison.Ordinal);
        Assert.Contains($"tokenUrl: \"{EntraBaseUrl}oauth2/v2.0/token\";", tsp, StringComparison.Ordinal);
        Assert.Contains("@useAuth(EntraOAuth2Auth<[\"User.Read\", \"Mail.Read\"]>)", tsp, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TypeSpecGenerator_ApiKeyAndBearer_UseMatchingAuth()
    {
        var apiKeyTsp = await GenerateTypeSpecAsync([Exchange("https://api.contoso.com/users", ("x-api-key", "secret-value"))]);
        Assert.Contains("@useAuth(ApiKeyAuth<ApiKeyLocation.header, \"x-api-key\">)", apiKeyTsp, StringComparison.Ordinal);

        var queryTsp = await GenerateTypeSpecAsync([Exchange("https://api.contoso.com/users?code=secret-value")]);
        Assert.Contains("@useAuth(ApiKeyAuth<ApiKeyLocation.query, \"code\">)", queryTsp, StringComparison.Ordinal);

        var bearerTsp = await GenerateTypeSpecAsync([Exchange("https://api.contoso.com/users", ("Authorization", "Bearer opaque-token"))]);
        Assert.Contains("@useAuth(BearerAuth)", bearerTsp, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TypeSpecGenerator_EntraV1AppToken_UsesLoginEndpoints()
    {
        var token = Jwt(EntraIssuerV1, roles: ["User.Read.All"]);
        var tsp = await GenerateTypeSpecAsync([Exchange("https://api.contoso.com/users", ("Authorization", $"Bearer {token}"))]);

        Assert.Contains("type: OAuth2FlowType.clientCredentials;", tsp, StringComparison.Ordinal);
        Assert.Contains($"tokenUrl: \"{EntraBaseUrl}oauth2/token\";", tsp, StringComparison.Ordinal);
        Assert.DoesNotContain("sts.windows.net", tsp, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("access_token")]
    [InlineData("token")]
    public async Task TypeSpecGenerator_AuthQueryParameter_UsesApiKeyAuth(string name)
    {
        var tsp = await GenerateTypeSpecAsync([Exchange($"https://api.contoso.com/users?{name}=abc")]);

        Assert.Contains($"ApiKeyAuth<ApiKeyLocation.query, \"{name}\">", tsp, StringComparison.Ordinal);
        Assert.DoesNotContain($"@query {name}", tsp, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TypeSpecGenerator_NonEntraJwt_UsesBearerAuth()
    {
        var token = Jwt("https://auth.contoso.com/", scp: "read");
        var tsp = await GenerateTypeSpecAsync([Exchange("https://api.contoso.com/users", ("Authorization", $"Bearer {token}"))]);

        Assert.Contains("@useAuth(BearerAuth)", tsp, StringComparison.Ordinal);
        Assert.DoesNotContain("APIOAuth2Auth", tsp, StringComparison.Ordinal);
    }
}
