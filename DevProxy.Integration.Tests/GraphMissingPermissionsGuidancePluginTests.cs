// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Net;
using System.Text;
using System.Text.Json;
using DevProxy.Abstractions.Proxy;
using DevProxy.Plugins.Guidance;
using Microsoft.Extensions.Logging;
using Xunit;

namespace DevProxy.Integration.Tests;

/// <summary>
/// Coverage for <see cref="GraphMissingPermissionsGuidancePlugin"/>. The Microsoft Graph
/// DevX permissions API is stubbed with <see cref="StubPermissionsHandler"/> so the tests
/// stay hermetic.
/// </summary>
public sealed class GraphMissingPermissionsGuidancePluginTests
{
    private const string MailPermissionsJson = """
        [
          { "value": "Mail.Read", "scopeType": "DelegatedWork", "isLeastPrivilege": false, "isHidden": false },
          { "value": "Mail.ReadBasic", "scopeType": "DelegatedWork", "isLeastPrivilege": true, "isHidden": false },
          { "value": "Mail.ReadWrite", "scopeType": "DelegatedWork", "isLeastPrivilege": false, "isHidden": false }
        ]
        """;

    private const string UserPermissionsJson = """
        [
          { "value": "User.Read.All", "scopeType": "Application", "isLeastPrivilege": false, "isHidden": false },
          { "value": "User.ReadBasic.All", "scopeType": "Application", "isLeastPrivilege": true, "isHidden": false }
        ]
        """;

    private const string SelfOnlyUserPermissionsJson = """
        [
          { "value": "User.Read", "scopeType": "DelegatedWork", "isLeastPrivilege": false, "isHidden": false },
          { "value": "User.Read.All", "scopeType": "DelegatedWork", "isLeastPrivilege": false, "isHidden": false },
          { "value": "User.ReadBasic.All", "scopeType": "DelegatedWork", "isLeastPrivilege": true, "isHidden": false }
        ]
        """;

    private static ISet<UrlToWatch> GraphWatch => KestrelProxyHarness.BuildUrlsToWatch("graph.microsoft.com");

    [Fact]
    public async Task Forbidden_DelegatedTokenMissingPermission_LogsFailedWithLeastPrivilegedPermission()
    {
        using var handler = new StubPermissionsHandler(MailPermissionsJson);
        var (plugin, factory) = CreatePlugin(handler);

        var exchange = TestExchange
            .Request("GET", "https://graph.microsoft.com/v1.0/me/messages", [("Authorization", BearerToken(scp: "User.Read openid"))])
            .WithResponse(HttpStatusCode.Forbidden);
        await plugin.AfterResponseAsync(exchange.ResponseArgs, CancellationToken.None);

        var log = Assert.Single(factory.LogsOfType(MessageType.Failed));
        Assert.Contains("least privileged permission: Mail.ReadBasic", log.Message, StringComparison.Ordinal);
        Assert.Contains("Mail.Read, Mail.ReadWrite", log.Message, StringComparison.Ordinal);
        Assert.Contains("openid, User.Read", log.Message, StringComparison.Ordinal);
        Assert.Contains("consent", log.Message, StringComparison.OrdinalIgnoreCase);

        var requestUri = Assert.Single(handler.Requests);
        Assert.Contains("scopeType=DelegatedWork", requestUri.Query, StringComparison.Ordinal);
        Assert.Contains("requesturl=%2Fme%2Fmessages", requestUri.Query, StringComparison.Ordinal);
        Assert.Contains("method=GET", requestUri.Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Forbidden_ApplicationTokenMissingPermission_LogsFailedWithAdminConsentInstructions()
    {
        using var handler = new StubPermissionsHandler(UserPermissionsJson);
        var (plugin, factory) = CreatePlugin(handler);

        var exchange = TestExchange
            .Request("GET", "https://graph.microsoft.com/v1.0/users/8e6b0bba-5bce-4b1b-9d5b-1f5e8b0d1a6c", [("Authorization", BearerToken(roles: ["Mail.Read"]))])
            .WithResponse(HttpStatusCode.Forbidden);
        await plugin.AfterResponseAsync(exchange.ResponseArgs, CancellationToken.None);

        var log = Assert.Single(factory.LogsOfType(MessageType.Failed));
        Assert.Contains("User.ReadBasic.All", log.Message, StringComparison.Ordinal);
        Assert.Contains("admin consent", log.Message, StringComparison.Ordinal);
        Assert.Contains("scopeType=Application", Assert.Single(handler.Requests).Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Forbidden_TokenHasSufficientPermission_LogsWarningAboutOtherCauses()
    {
        using var handler = new StubPermissionsHandler(MailPermissionsJson);
        var (plugin, factory) = CreatePlugin(handler);

        var exchange = TestExchange
            .Request("GET", "https://graph.microsoft.com/v1.0/me/messages", [("Authorization", BearerToken(scp: "mail.read"))])
            .WithResponse(HttpStatusCode.Forbidden);
        await plugin.AfterResponseAsync(exchange.ResponseArgs, CancellationToken.None);

        Assert.Empty(factory.LogsOfType(MessageType.Failed));
        var log = Assert.Single(factory.LogsOfType(MessageType.Warning));
        Assert.Contains("Mail.Read", log.Message, StringComparison.Ordinal);
        Assert.Contains("another reason", log.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Forbidden_NoToken_LogsWarningWithoutCallingApi()
    {
        using var handler = new StubPermissionsHandler(MailPermissionsJson);
        var (plugin, factory) = CreatePlugin(handler);

        var exchange = TestExchange
            .Request("GET", "https://graph.microsoft.com/v1.0/me/messages")
            .WithResponse(HttpStatusCode.Forbidden);
        await plugin.AfterResponseAsync(exchange.ResponseArgs, CancellationToken.None);

        var log = Assert.Single(factory.LogsOfType(MessageType.Warning));
        Assert.Contains("access token", log.Message, StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Forbidden_PermissionsUnknown_LogsWarning()
    {
        using var handler = new StubPermissionsHandler(null, HttpStatusCode.NotFound);
        var (plugin, factory) = CreatePlugin(handler);

        var exchange = TestExchange
            .Request("GET", "https://graph.microsoft.com/v1.0/foo/bar", [("Authorization", BearerToken(scp: "User.Read"))])
            .WithResponse(HttpStatusCode.Forbidden);
        await plugin.AfterResponseAsync(exchange.ResponseArgs, CancellationToken.None);

        var log = Assert.Single(factory.LogsOfType(MessageType.Warning));
        Assert.Contains("couldn't determine", log.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NonForbiddenResponse_IsIgnored()
    {
        using var handler = new StubPermissionsHandler(MailPermissionsJson);
        var (plugin, factory) = CreatePlugin(handler);

        var exchange = TestExchange
            .Request("GET", "https://graph.microsoft.com/v1.0/me/messages", [("Authorization", BearerToken(scp: "User.Read"))])
            .WithResponse(HttpStatusCode.Unauthorized);
        await plugin.AfterResponseAsync(exchange.ResponseArgs, CancellationToken.None);

        Assert.Empty(factory.LogsOfType(MessageType.Failed));
        Assert.Empty(factory.LogsOfType(MessageType.Warning));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task RepeatedForbiddenRequests_UseCachedPermissions()
    {
        using var handler = new StubPermissionsHandler(MailPermissionsJson);
        var (plugin, factory) = CreatePlugin(handler);

        foreach (var id in new[] { "0b8a5d2e-1c3f-4e5a-9b7c-6d8e9f0a1b2c", "4f3e2d1c-0b9a-4876-a543-210fedcba987" })
        {
            var exchange = TestExchange
                .Request("GET", $"https://graph.microsoft.com/v1.0/me/messages/{id}", [("Authorization", BearerToken(scp: "User.Read"))])
                .WithResponse(HttpStatusCode.Forbidden);
            await plugin.AfterResponseAsync(exchange.ResponseArgs, CancellationToken.None);
        }

        Assert.Equal(2, factory.LogsOfType(MessageType.Failed).Count);
        _ = Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Batch_ForbiddenInnerResponse_LogsFailedForThatRequest()
    {
        using var handler = new StubPermissionsHandler(MailPermissionsJson);
        var (plugin, factory) = CreatePlugin(handler);

        var batchRequest = """
            {
              "requests": [
                { "id": "1", "method": "GET", "url": "/me" },
                { "id": "2", "method": "GET", "url": "/me/messages" }
              ]
            }
            """;
        var batchResponse = """
            {
              "responses": [
                { "id": "1", "status": 200, "body": {} },
                { "id": "2", "status": 403, "body": { "error": { "code": "ErrorAccessDenied", "message": "Access is denied." } } }
              ]
            }
            """;

        var exchange = TestExchange
            .Request("POST", "https://graph.microsoft.com/v1.0/$batch", [("Authorization", BearerToken(scp: "User.Read")), ("Content-Type", "application/json")], batchRequest)
            .WithResponse(HttpStatusCode.OK, [("Content-Type", "application/json")], batchResponse);
        await plugin.AfterResponseAsync(exchange.ResponseArgs, CancellationToken.None);

        var log = Assert.Single(factory.LogsOfType(MessageType.Failed));
        Assert.Contains("Batch request 2", log.Message, StringComparison.Ordinal);
        Assert.Contains("Mail.ReadBasic", log.Message, StringComparison.Ordinal);
        Assert.Contains("requesturl=%2Fme%2Fmessages", Assert.Single(handler.Requests).Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MockedForbiddenResponse_IsIgnored()
    {
        using var handler = new StubPermissionsHandler(MailPermissionsJson);
        var (plugin, factory) = CreatePlugin(handler);

        var exchange = TestExchange
            .Request("GET", "https://graph.microsoft.com/v1.0/me/messages", [("Authorization", BearerToken(scp: "User.Read"))]);
        exchange.Session.Respond("{}", HttpStatusCode.Forbidden, []);
        await plugin.AfterResponseAsync(exchange.ResponseArgs, CancellationToken.None);

        Assert.Empty(factory.LogsOfType(MessageType.Failed));
        Assert.Empty(factory.LogsOfType(MessageType.Warning));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Forbidden_DodGraphHost_LogsFailed()
    {
        using var handler = new StubPermissionsHandler(MailPermissionsJson);
        var factory = new CapturingLoggerFactory();
#pragma warning disable CA2000 // HttpClient doesn't own the handler; the test disposes it
        var plugin = new GraphMissingPermissionsGuidancePlugin(
            new HttpClient(handler, disposeHandler: false),
            new Logger<GraphMissingPermissionsGuidancePlugin>(factory),
            KestrelProxyHarness.BuildUrlsToWatch("dod-graph.microsoft.us"));
#pragma warning restore CA2000

        var exchange = TestExchange
            .Request("GET", "https://dod-graph.microsoft.us/v1.0/me/messages", [("Authorization", BearerToken(scp: "User.Read"))])
            .WithResponse(HttpStatusCode.Forbidden);
        await plugin.AfterResponseAsync(exchange.ResponseArgs, CancellationToken.None);

        _ = Assert.Single(factory.LogsOfType(MessageType.Failed));
    }

    [Fact]
    public async Task Batch_ConcurrentForbiddenRequestsWithSameKey_ShareSingleLookup()
    {
        using var handler = new StubPermissionsHandler(MailPermissionsJson) { Delay = TimeSpan.FromMilliseconds(200) };
        var (plugin, factory) = CreatePlugin(handler);

        var ids = new[] { "0b8a5d2e-1c3f-4e5a-9b7c-6d8e9f0a1b2c", "4f3e2d1c-0b9a-4876-a543-210fedcba987", "9a8b7c6d-5e4f-4a3b-8c2d-1e0f9a8b7c6d" };
        var batchRequest = JsonSerializer.Serialize(new
        {
            requests = ids.Select((id, i) => new { id = $"{i + 1}", method = "GET", url = $"/me/messages/{id}" })
        });
        var batchResponse = JsonSerializer.Serialize(new
        {
            responses = ids.Select((_, i) => new { id = $"{i + 1}", status = 403, body = new { } })
        });

        var exchange = TestExchange
            .Request("POST", "https://graph.microsoft.com/v1.0/$batch", [("Authorization", BearerToken(scp: "User.Read")), ("Content-Type", "application/json")], batchRequest)
            .WithResponse(HttpStatusCode.OK, [("Content-Type", "application/json")], batchResponse);
        await plugin.AfterResponseAsync(exchange.ResponseArgs, CancellationToken.None);

        Assert.Equal(3, factory.LogsOfType(MessageType.Failed).Count);
        _ = Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task FailedLookup_IsRetriedOnNextRequest()
    {
        using var handler = new StubPermissionsHandler(MailPermissionsJson) { FailuresBeforeSuccess = 1 };
        var (plugin, factory) = CreatePlugin(handler);

        for (var i = 0; i < 2; i++)
        {
            var exchange = TestExchange
                .Request("GET", "https://graph.microsoft.com/v1.0/me/messages", [("Authorization", BearerToken(scp: "User.Read"))])
                .WithResponse(HttpStatusCode.Forbidden);
            await plugin.AfterResponseAsync(exchange.ResponseArgs, CancellationToken.None);
        }

        _ = Assert.Single(factory.LogsOfType(MessageType.Failed));
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task Forbidden_DelegatedSelfOnlyPermissionForOtherUser_LogsFailed()
    {
        using var handler = new StubPermissionsHandler(SelfOnlyUserPermissionsJson);
        var (plugin, factory) = CreatePlugin(handler);

        var exchange = TestExchange
            .Request("GET", "https://graph.microsoft.com/v1.0/users/8e6b0bba-5bce-4b1b-9d5b-1f5e8b0d1a6c", [("Authorization", BearerToken(scp: "User.Read", oid: "1d2c3b4a-5e6f-4a7b-8c9d-0e1f2a3b4c5d"))])
            .WithResponse(HttpStatusCode.Forbidden);
        await plugin.AfterResponseAsync(exchange.ResponseArgs, CancellationToken.None);

        Assert.Empty(factory.LogsOfType(MessageType.Warning));
        var log = Assert.Single(factory.LogsOfType(MessageType.Failed));
        Assert.Contains("least privileged permission: User.ReadBasic.All", log.Message, StringComparison.Ordinal);
        Assert.Contains("higher-privileged permissions: User.Read.All.", log.Message, StringComparison.Ordinal);
        Assert.Contains("only allow access to the signed-in user", log.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Forbidden_DelegatedSelfOnlyPermissionForSignedInUser_LogsWarningAboutOtherCauses()
    {
        using var handler = new StubPermissionsHandler(SelfOnlyUserPermissionsJson);
        var (plugin, factory) = CreatePlugin(handler);

        const string signedInUserId = "8e6b0bba-5bce-4b1b-9d5b-1f5e8b0d1a6c";
        var exchange = TestExchange
            .Request("GET", $"https://graph.microsoft.com/v1.0/users/{signedInUserId}", [("Authorization", BearerToken(scp: "User.Read", oid: signedInUserId))])
            .WithResponse(HttpStatusCode.Forbidden);
        await plugin.AfterResponseAsync(exchange.ResponseArgs, CancellationToken.None);

        Assert.Empty(factory.LogsOfType(MessageType.Failed));
        var log = Assert.Single(factory.LogsOfType(MessageType.Warning));
        Assert.Contains("User.Read", log.Message, StringComparison.Ordinal);
        Assert.Contains("another reason", log.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("https://graph.microsoft.com/v1.0/users('8e6b0bba-5bce-4b1b-9d5b-1f5e8b0d1a6c')")]
    [InlineData("https://graph.microsoft.com/v1.0/users('john@contoso.com')/messages")]
    public async Task Forbidden_UnparsableUrl_LogsTip(string url)
    {
        using var handler = new StubPermissionsHandler(MailPermissionsJson);
        var (plugin, factory) = CreatePlugin(handler);

        var exchange = TestExchange
            .Request("GET", url, [("Authorization", BearerToken(scp: "User.Read"))])
            .WithResponse(HttpStatusCode.Forbidden);
        await plugin.AfterResponseAsync(exchange.ResponseArgs, CancellationToken.None);

        var log = Assert.Single(factory.LogsOfType(MessageType.Tip));
        Assert.Contains("couldn't analyze missing permissions", log.Message, StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Batch_ForbiddenInnerResponseWithUnparsableUrl_LogsTip()
    {
        using var handler = new StubPermissionsHandler(MailPermissionsJson);
        var (plugin, factory) = CreatePlugin(handler);

        var batchRequest = """
            { "requests": [ { "id": "1", "method": "GET", "url": "/users('john@contoso.com')/messages" } ] }
            """;
        var batchResponse = """
            { "responses": [ { "id": "1", "status": 403, "body": {} } ] }
            """;

        var exchange = TestExchange
            .Request("POST", "https://graph.microsoft.com/v1.0/$batch", [("Authorization", BearerToken(scp: "User.Read")), ("Content-Type", "application/json")], batchRequest)
            .WithResponse(HttpStatusCode.OK, [("Content-Type", "application/json")], batchResponse);
        await plugin.AfterResponseAsync(exchange.ResponseArgs, CancellationToken.None);

        var log = Assert.Single(factory.LogsOfType(MessageType.Tip));
        Assert.Contains("Batch request 1", log.Message, StringComparison.Ordinal);
        Assert.Contains("couldn't analyze missing permissions", log.Message, StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Forbidden_PersonalAccountToken_UsesDelegatedPersonalPermissions()
    {
        using var handler = new StubPermissionsHandler(MailPermissionsJson);
        var (plugin, factory) = CreatePlugin(handler);

        var exchange = TestExchange
            .Request("GET", "https://graph.microsoft.com/v1.0/me/messages", [("Authorization", BearerToken(scp: "User.Read", tid: "9188040d-6c67-4c5b-b112-36a304b66dad"))])
            .WithResponse(HttpStatusCode.Forbidden);
        await plugin.AfterResponseAsync(exchange.ResponseArgs, CancellationToken.None);

        _ = Assert.Single(factory.LogsOfType(MessageType.Failed));
        Assert.Contains("scopeType=DelegatedPersonal", Assert.Single(handler.Requests).Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Forbidden_DelegatedOtherUserData_IncludesSharingNote()
    {
        using var handler = new StubPermissionsHandler(MailPermissionsJson);
        var (plugin, factory) = CreatePlugin(handler);

        var exchange = TestExchange
            .Request("GET", "https://graph.microsoft.com/v1.0/users/8e6b0bba-5bce-4b1b-9d5b-1f5e8b0d1a6c/messages", [("Authorization", BearerToken(scp: "User.Read", oid: "1d2c3b4a-5e6f-4a7b-8c9d-0e1f2a3b4c5d"))])
            .WithResponse(HttpStatusCode.Forbidden);
        await plugin.AfterResponseAsync(exchange.ResponseArgs, CancellationToken.None);

        var log = Assert.Single(factory.LogsOfType(MessageType.Failed));
        Assert.Contains("Mail.ReadBasic", log.Message, StringComparison.Ordinal);
        Assert.Contains("Mail.Read.Shared", log.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Forbidden_DelegatedOtherUserDirectoryData_OmitsSharingNote()
    {
        using var handler = new StubPermissionsHandler("""
            [
              { "value": "GroupMember.Read.All", "scopeType": "DelegatedWork", "isLeastPrivilege": true, "isHidden": false },
              { "value": "Directory.Read.All", "scopeType": "DelegatedWork", "isLeastPrivilege": false, "isHidden": false }
            ]
            """);
        var (plugin, factory) = CreatePlugin(handler);

        var exchange = TestExchange
            .Request("GET", "https://graph.microsoft.com/v1.0/users/8e6b0bba-5bce-4b1b-9d5b-1f5e8b0d1a6c/memberOf", [("Authorization", BearerToken(scp: "User.Read", oid: "1d2c3b4a-5e6f-4a7b-8c9d-0e1f2a3b4c5d"))])
            .WithResponse(HttpStatusCode.Forbidden);
        await plugin.AfterResponseAsync(exchange.ResponseArgs, CancellationToken.None);

        var log = Assert.Single(factory.LogsOfType(MessageType.Failed));
        Assert.DoesNotContain(".Shared", log.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Forbidden_DelegatedSignedInUserData_OmitsSharingNote()
    {
        using var handler = new StubPermissionsHandler(MailPermissionsJson);
        var (plugin, factory) = CreatePlugin(handler);

        const string signedInUserId = "8e6b0bba-5bce-4b1b-9d5b-1f5e8b0d1a6c";
        var exchange = TestExchange
            .Request("GET", $"https://graph.microsoft.com/v1.0/users/{signedInUserId}/messages", [("Authorization", BearerToken(scp: "User.Read", oid: signedInUserId))])
            .WithResponse(HttpStatusCode.Forbidden);
        await plugin.AfterResponseAsync(exchange.ResponseArgs, CancellationToken.None);

        var log = Assert.Single(factory.LogsOfType(MessageType.Failed));
        Assert.DoesNotContain(".Shared", log.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Forbidden_NoLeastPrivilegedPermissionMarked_ListsUnrankedAlternatives()
    {
        using var handler = new StubPermissionsHandler("""
            [
              { "value": "User.Read.All", "scopeType": "Application", "isLeastPrivilege": false, "isHidden": false },
              { "value": "User.ReadBasic.All", "scopeType": "Application", "isLeastPrivilege": false, "isHidden": false }
            ]
            """);
        var (plugin, factory) = CreatePlugin(handler);

        var exchange = TestExchange
            .Request("GET", "https://graph.microsoft.com/v1.0/users", [("Authorization", BearerToken(roles: ["Mail.Read"]))])
            .WithResponse(HttpStatusCode.Forbidden);
        await plugin.AfterResponseAsync(exchange.ResponseArgs, CancellationToken.None);

        var log = Assert.Single(factory.LogsOfType(MessageType.Failed));
        Assert.Contains("Add any of the following permissions: User.Read.All, User.ReadBasic.All.", log.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("least privileged", log.Message, StringComparison.Ordinal);
    }

    private static (GraphMissingPermissionsGuidancePlugin Plugin, CapturingLoggerFactory Factory) CreatePlugin(HttpMessageHandler handler)
    {
        var factory = new CapturingLoggerFactory();
#pragma warning disable CA2000 // HttpClient doesn't own the handler; the test disposes it
        var httpClient = new HttpClient(handler, disposeHandler: false);
#pragma warning restore CA2000
        var plugin = new GraphMissingPermissionsGuidancePlugin(
            httpClient,
            new Logger<GraphMissingPermissionsGuidancePlugin>(factory),
            GraphWatch);
        return (plugin, factory);
    }

    private static string BearerToken(string? scp = null, string[]? roles = null, string? oid = null, string? tid = null)
    {
        var payload = new Dictionary<string, object> { ["aud"] = "https://graph.microsoft.com" };
        if (tid is not null)
        {
            payload["tid"] = tid;
        }
        if (oid is not null)
        {
            payload["oid"] = oid;
        }
        if (scp is not null)
        {
            payload["scp"] = scp;
        }
        if (roles is not null)
        {
            payload["roles"] = roles;
        }

        static string Encode(string value) => Convert.ToBase64String(Encoding.UTF8.GetBytes(value))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

        return $"Bearer {Encode("""{"alg":"none","typ":"JWT"}""")}.{Encode(JsonSerializer.Serialize(payload))}.signature";
    }

    private sealed class StubPermissionsHandler(string? responseJson, HttpStatusCode statusCode = HttpStatusCode.OK) : HttpMessageHandler
    {
        private readonly Lock _lock = new();
        private readonly List<Uri> _requests = [];

        public IReadOnlyList<Uri> Requests
        {
            get
            {
                lock (_lock)
                {
                    return [.. _requests];
                }
            }
        }

        public TimeSpan Delay { get; init; } = TimeSpan.Zero;

        public int FailuresBeforeSuccess { get; init; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            int requestCount;
            lock (_lock)
            {
                _requests.Add(request.RequestUri!);
                requestCount = _requests.Count;
            }

            if (Delay > TimeSpan.Zero)
            {
                await Task.Delay(Delay, cancellationToken);
            }

            return new HttpResponseMessage(requestCount <= FailuresBeforeSuccess ? HttpStatusCode.InternalServerError : statusCode)
            {
                Content = new StringContent(responseJson ?? "{}", Encoding.UTF8, "application/json")
            };
        }
    }
}
