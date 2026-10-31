// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Net;
using DevProxy.Abstractions.Proxy;
using DevProxy.Abstractions.Utils;
using DevProxy.Plugins.Reporting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DevProxy.Integration.Tests;

/// <summary>
/// Coverage for <see cref="OpenApiValidationPlugin"/>, which validates intercepted
/// requests and responses against local OpenAPI specs.
/// </summary>
public sealed class OpenApiValidationPluginTests : IDisposable
{
    private const string Spec = """
    {
      "openapi": "3.0.3",
      "info": { "title": "Contoso", "version": "1.0" },
      "servers": [ { "url": "https://api.contoso.com/v1" } ],
      "paths": {
        "/users": {
          "get": {
            "parameters": [
              { "name": "top", "in": "query", "schema": { "type": "integer" } },
              { "name": "status", "in": "query", "schema": { "type": "string", "enum": [ "active", "inactive" ] } },
              { "name": "x-tenant", "in": "header", "required": true, "schema": { "type": "string" } }
            ],
            "responses": {
              "200": {
                "description": "ok",
                "headers": {
                  "x-total-count": { "required": true, "schema": { "type": "integer" } }
                },
                "content": {
                  "application/json": {
                    "schema": { "type": "array", "items": { "$ref": "#/components/schemas/User" } }
                  }
                }
              },
              "4XX": {
                "description": "client error",
                "content": { "application/json": { "schema": { "$ref": "#/components/schemas/Error" } } }
              }
            }
          },
          "post": {
            "requestBody": {
              "required": true,
              "content": {
                "application/json": { "schema": { "$ref": "#/components/schemas/User" } }
              }
            },
            "responses": {
              "201": {
                "description": "created",
                "content": { "application/json": { "schema": { "$ref": "#/components/schemas/User" } } }
              }
            }
          }
        },
        "/users/{id}": {
          "parameters": [ { "name": "id", "in": "path", "required": true, "schema": { "type": "integer" } } ],
          "get": {
            "responses": {
              "200": {
                "description": "ok",
                "content": { "application/json": { "schema": { "$ref": "#/components/schemas/User" } } }
              }
            }
          }
        },
        "/users/me": {
          "get": {
            "responses": {
              "200": {
                "description": "ok",
                "content": { "application/json": { "schema": { "$ref": "#/components/schemas/User" } } }
              }
            }
          }
        },
        "/pets": {
          "get": {
            "responses": {
              "default": {
                "description": "pets",
                "content": {
                  "application/json": {
                    "schema": {
                      "type": "array",
                      "items": {
                        "oneOf": [ { "$ref": "#/components/schemas/Cat" }, { "$ref": "#/components/schemas/Dog" } ]
                      }
                    }
                  }
                }
              }
            }
          }
        }
      },
      "components": {
        "schemas": {
          "User": {
            "type": "object",
            "required": [ "id", "name" ],
            "additionalProperties": false,
            "properties": {
              "id": { "type": "integer", "readOnly": true },
              "name": { "type": "string" },
              "nickname": { "type": "string", "nullable": true },
              "role": { "type": "string", "enum": [ "admin", "user" ] },
              "tags": { "type": "array", "items": { "type": "string" } }
            }
          },
          "Error": {
            "type": "object",
            "required": [ "message" ],
            "properties": { "message": { "type": "string" } }
          },
          "Cat": {
            "type": "object",
            "required": [ "meows" ],
            "additionalProperties": false,
            "properties": { "meows": { "type": "boolean" } }
          },
          "Dog": {
            "type": "object",
            "required": [ "barks" ],
            "additionalProperties": false,
            "properties": { "barks": { "type": "boolean" } }
          }
        }
      }
    }
    """;

    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("devproxy-oasvalidation-");

    public void Dispose() => _dir.Delete(recursive: true);

    private static RecordingArgs Recording(IEnumerable<RequestLog> logs) =>
        new(logs)
        {
            GlobalData = new() { [ProxyUtils.ReportsKey] = new Dictionary<string, object>() },
        };

    private async Task<OpenApiValidationPlugin> CreatePluginAsync(string configJson = """{ "specFiles": [ "contoso.json" ] }""",
        ILogger<OpenApiValidationPlugin>? logger = null)
    {
        await File.WriteAllTextAsync(Path.Combine(_dir.FullName, "contoso.json"), Spec);

        var proxyConfig = new TestProxyConfiguration
        {
            ConfigFile = Path.Combine(_dir.FullName, "devproxyrc.json"),
        };
        var plugin = new OpenApiValidationPlugin(
            TestDefaults.HttpClient,
            logger ?? NullLogger<OpenApiValidationPlugin>.Instance,
            KestrelProxyHarness.BuildUrlsToWatch("api.contoso.com"),
            proxyConfig,
            PluginConfig.FromJson(configJson));

        using var host = new PluginTestHost(proxyConfig);
        await plugin.InitializeAsync(host.CreateInitArgs(), CancellationToken.None);
        return plugin;
    }

    private static TestExchange Get(string url, HttpStatusCode status, string? body,
        IEnumerable<(string, string)>? requestHeaders = null,
        IEnumerable<(string, string)>? responseHeaders = null) =>
        TestExchange
            .Request("GET", url, headers: requestHeaders)
            .WithResponse(
                status,
                headers: [("Content-Type", "application/json"), .. responseHeaders ?? []],
                body: body);

    private static string[] Messages(OpenApiValidationResult? result) =>
        [.. Assert.IsType<OpenApiValidationResult>(result).Issues.Select(i => i.ToString())];

    [Fact]
    public async Task ValidRequestAndResponse_HasNoIssues()
    {
        var plugin = await CreatePluginAsync();
        var exchange = Get("https://api.contoso.com/v1/users?top=10&status=active", HttpStatusCode.OK,
            """[ { "id": 1, "name": "Alex", "nickname": null, "role": "admin", "tags": [ "a" ] } ]""",
            requestHeaders: [("x-tenant", "contoso")],
            responseHeaders: [("x-total-count", "1")]);

        var result = plugin.Validate(exchange.Session);

        Assert.NotNull(result);
        Assert.Equal("GET /users", result.Operation);
        Assert.Empty(result.Issues);
    }

    [Fact]
    public async Task ReportsParameterIssues()
    {
        var plugin = await CreatePluginAsync();
        var exchange = Get("https://api.contoso.com/v1/users?top=abc&status=deleted&foo=bar", HttpStatusCode.OK,
            "[]", responseHeaders: [("x-total-count", "0")]);

        var messages = Messages(plugin.Validate(exchange.Session));

        Assert.Contains(messages, m => m.Contains("Missing required header parameter 'x-tenant'", StringComparison.Ordinal));
        Assert.Contains(messages, m => m.StartsWith("top:", StringComparison.Ordinal) && m.Contains("Expected integer", StringComparison.Ordinal));
        Assert.Contains(messages, m => m.StartsWith("status:", StringComparison.Ordinal) && m.Contains("allowed values", StringComparison.Ordinal));
        Assert.Contains(messages, m => m.Contains("Query parameter 'foo' isn't documented", StringComparison.Ordinal));
        Assert.Equal(4, messages.Length);
    }

    [Fact]
    public async Task ReportsInvalidPathParameter()
    {
        var plugin = await CreatePluginAsync();
        var exchange = Get("https://api.contoso.com/v1/users/abc", HttpStatusCode.OK, """{ "id": 1, "name": "Alex" }""");

        var result = plugin.Validate(exchange.Session);

        Assert.Equal("GET /users/{id}", result?.Operation);
        var issue = Assert.Single(result!.Issues);
        Assert.Equal("id", issue.Path);
        Assert.Contains("Expected integer", issue.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PrefersLiteralPathOverTemplate()
    {
        var plugin = await CreatePluginAsync();
        var exchange = Get("https://api.contoso.com/v1/users/me", HttpStatusCode.OK, """{ "id": 1, "name": "Alex" }""");

        var result = plugin.Validate(exchange.Session);

        Assert.Equal("GET /users/me", result?.Operation);
        Assert.Empty(result!.Issues);
    }

    [Fact]
    public async Task ReportsUndocumentedPathAndMethod()
    {
        var plugin = await CreatePluginAsync();

        var path = plugin.Validate(Get("https://api.contoso.com/v1/orders", HttpStatusCode.OK, "[]").Session);
        var method = plugin.Validate(TestExchange.Request("DELETE", "https://api.contoso.com/v1/users").WithResponse(HttpStatusCode.NoContent).Session);

        Assert.Contains("Path '/orders' isn't documented", Assert.Single(Messages(path)), StringComparison.Ordinal);
        Assert.Contains("Method DELETE isn't documented for path '/users'", Assert.Single(Messages(method)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReportsRequestBodyIssues()
    {
        var plugin = await CreatePluginAsync();
        var exchange = TestExchange
            .Request("POST", "https://api.contoso.com/v1/users",
                headers: [("Content-Type", "application/json")],
                body: """{ "name": 42, "role": "guest", "unknown": true }""")
            .WithResponse(HttpStatusCode.Created, headers: [("Content-Type", "application/json")],
                body: """{ "id": 1, "name": "Alex" }""");

        var result = plugin.Validate(exchange.Session);
        var messages = Messages(result);

        // id is readOnly so it's not required in the request
        Assert.DoesNotContain(messages, m => m.Contains("'id'", StringComparison.Ordinal));
        Assert.Contains("$.name: Expected string but got integer", messages);
        Assert.Contains(messages, m => m.StartsWith("$.role:", StringComparison.Ordinal));
        Assert.Contains("$.unknown: Property 'unknown' isn't defined in the schema", messages);
        Assert.All(result!.Issues, i => Assert.Equal(OpenApiValidationIssueLocation.Request, i.Location));
    }

    [Fact]
    public async Task ReportsMissingRequiredRequestBodyAndUndocumentedContentType()
    {
        var plugin = await CreatePluginAsync();

        var missing = plugin.Validate(TestExchange.Request("POST", "https://api.contoso.com/v1/users")
            .WithResponse(HttpStatusCode.Created).Session);
        var xml = plugin.Validate(TestExchange.Request("POST", "https://api.contoso.com/v1/users",
                headers: [("Content-Type", "application/xml")], body: "<user />")
            .WithResponse(HttpStatusCode.Created).Session);

        Assert.Equal("Missing required request body", Assert.Single(Messages(missing)));
        Assert.Contains("Content type 'application/xml' isn't documented", Assert.Single(Messages(xml)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReportsResponseIssues()
    {
        var plugin = await CreatePluginAsync();
        var headers = new[] { ("x-tenant", "contoso") };

        var undocumentedStatus = plugin.Validate(Get("https://api.contoso.com/v1/users", HttpStatusCode.InternalServerError, "{}", headers).Session);
        var badBody = plugin.Validate(Get("https://api.contoso.com/v1/users", HttpStatusCode.OK, """[ { "id": "1" } ]""", headers,
            responseHeaders: [("x-total-count", "1")]).Session);
        var missingHeader = plugin.Validate(Get("https://api.contoso.com/v1/users", HttpStatusCode.OK, "[]", headers).Session);
        var range = plugin.Validate(Get("https://api.contoso.com/v1/users", HttpStatusCode.NotFound, """{ "message": "nope" }""", headers).Session);

        Assert.Contains("Status code 500 isn't documented", Assert.Single(Messages(undocumentedStatus)), StringComparison.Ordinal);
        var badBodyMessages = Messages(badBody);
        Assert.Contains("$[0].id: Expected integer but got string", badBodyMessages);
        Assert.Contains("$[0]: Missing required property 'name'", badBodyMessages);
        Assert.Contains("Missing required response header 'x-total-count'", Assert.Single(Messages(missingHeader)), StringComparison.Ordinal);
        Assert.Empty(range!.Issues);
    }

    [Fact]
    public async Task ValidatesOneOf()
    {
        var plugin = await CreatePluginAsync();

        var valid = plugin.Validate(Get("https://api.contoso.com/v1/pets", HttpStatusCode.OK, """[ { "meows": true }, { "barks": false } ]""").Session);
        var invalid = plugin.Validate(Get("https://api.contoso.com/v1/pets", HttpStatusCode.OK, """[ { "quacks": true } ]""").Session);

        Assert.Empty(valid!.Issues);
        Assert.Contains("$[0]: Value doesn't match any of the schemas in 'oneOf'", Messages(invalid));
    }

    [Fact]
    public async Task IgnoresRequestsNotMatchingAnySpec()
    {
        var plugin = await CreatePluginAsync();

        Assert.Null(plugin.Validate(Get("https://api.contoso.com/v2/users", HttpStatusCode.OK, "[]").Session));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StoresReportOnRecordingStop(bool reportUnmatchedRequests)
    {
        var plugin = await CreatePluginAsync($$"""{ "specsFolder": ".", "reportUnmatchedRequests": {{(reportUnmatchedRequests ? "true" : "false")}} }""");
        var args = Recording([
            Get("https://api.contoso.com/v1/users/1", HttpStatusCode.OK, """{ "id": 1, "name": "Alex" }""").AsRequestLog(MessageType.InterceptedResponse),
            Get("https://api.contoso.com/v1/users/1", HttpStatusCode.OK, """{ "id": 1 }""").AsRequestLog(MessageType.InterceptedResponse),
            Get("https://api.contoso.com/other", HttpStatusCode.OK, "{}").AsRequestLog(MessageType.InterceptedResponse),
        ]);

        await plugin.AfterRecordingStopAsync(args, CancellationToken.None);

        var report = Assert.IsType<OpenApiValidationPluginReport>(
            ((Dictionary<string, object>)args.GlobalData[ProxyUtils.ReportsKey])[plugin.Name]);
        Assert.Equal(2, report.ValidatedRequests);
        var result = Assert.Single(report.Results);
        Assert.Equal("GET /users/{id}", result.Operation);
        if (reportUnmatchedRequests)
        {
            Assert.Equal(["GET https://api.contoso.com/other"], report.UnmatchedRequests);
        }
        else
        {
            Assert.Null(report.UnmatchedRequests);
        }
        Assert.Contains("Missing required property 'name'", report.ToMarkdown(), StringComparison.Ordinal);
        Assert.Contains("Missing required property 'name'", report.ToPlainText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReportsSkippedValidations()
    {
        var plugin = await CreatePluginAsync();
        var args = Recording([
            Get("https://api.contoso.com/v1/users/1", HttpStatusCode.OK, """{ "id": 1, "name": "Alex" }""").AsRequestLog(MessageType.InterceptedResponse),
            Get("https://api.contoso.com/v1/users/2", HttpStatusCode.OK, $$"""{ "id": 2, "name": "{{new string('a', 5 * 1024 * 1024)}}" }""").AsRequestLog(MessageType.InterceptedResponse),
        ]);

        await plugin.AfterRecordingStopAsync(args, CancellationToken.None);

        var report = Assert.IsType<OpenApiValidationPluginReport>(
            ((Dictionary<string, object>)args.GlobalData[ProxyUtils.ReportsKey])[plugin.Name]);
        Assert.Equal(2, report.ValidatedRequests);
        Assert.Empty(report.Results);
        var skipped = Assert.Single(report.SkippedValidations!);
        Assert.StartsWith("GET https://api.contoso.com/v1/users/2: Skipped validating the response body", skipped, StringComparison.Ordinal);
        var markdown = report.ToMarkdown()!;
        Assert.DoesNotContain("All validated requests", markdown, StringComparison.Ordinal);
        Assert.Contains("## Skipped validations", markdown, StringComparison.Ordinal);
        Assert.Contains(skipped, report.ToPlainText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task LoadsYamlOpenApi31SpecsFromFolder()
    {
        var specsDir = Directory.CreateDirectory(Path.Combine(_dir.FullName, "specs", "nested"));
        await File.WriteAllTextAsync(Path.Combine(specsDir.FullName, "fabrikam.yaml"), """
        openapi: 3.1.0
        info:
          title: Fabrikam
          version: '1.0'
        servers:
          - url: https://{region}.fabrikam.com
            variables:
              region:
                default: eu
        paths:
          /items:
            get:
              responses:
                '200':
                  description: ok
                  content:
                    application/json:
                      schema:
                        type: object
                        properties:
                          name:
                            type: [string, 'null']
        """);
        var plugin = await CreatePluginAsync("""{ "specsFolder": "specs" }""");

        var valid = plugin.Validate(TestExchange.Request("GET", "https://eu.fabrikam.com/items")
            .WithResponse(HttpStatusCode.OK, headers: [("Content-Type", "application/json; charset=utf-8")], body: """{ "name": null }""").Session);
        var invalid = plugin.Validate(TestExchange.Request("GET", "https://eu.fabrikam.com/items")
            .WithResponse(HttpStatusCode.OK, headers: [("Content-Type", "application/json")], body: """{ "name": 1 }""").Session);

        Assert.Equal("GET /items", valid?.Operation);
        Assert.Empty(valid!.Issues);
        Assert.Contains("$.name: Expected string or null but got integer", Messages(invalid));
    }

    [Fact]
    public async Task ThrowsWhenNoSpecsConfigured()
    {
        _ = await Assert.ThrowsAsync<InvalidOperationException>(() => CreatePluginAsync("{}"));
    }

    private async Task<OpenApiValidationPlugin> CreatePluginWithSpecsAsync(params (string Name, string Content)[] specs)
    {
        foreach (var (name, content) in specs)
        {
            await File.WriteAllTextAsync(Path.Combine(_dir.FullName, name), content);
        }
        var files = string.Join(", ", specs.Select(s => $"\"{s.Name}\""));
        return await CreatePluginAsync($$"""{ "specFiles": [ {{files}} ] }""");
    }

    private static TestExchange Call(string method, string url, IEnumerable<(string, string)>? requestHeaders = null) =>
        TestExchange.Request(method, url, headers: requestHeaders).WithResponse(HttpStatusCode.NoContent);

    [Fact]
    public async Task FindsMethodAcrossSpecsSharingServerAndPath()
    {
        var plugin = await CreatePluginWithSpecsAsync(
            ("read.yaml", """
            openapi: 3.0.3
            info: { title: Read, version: '1.0' }
            servers:
              - url: https://api.fabrikam.com
            paths:
              /users:
                get:
                  responses: { '204': { description: ok } }
            """),
            ("write.yaml", """
            openapi: 3.0.3
            info: { title: Write, version: '1.0' }
            servers:
              - url: https://api.fabrikam.com
            paths:
              /users:
                post:
                  responses: { '204': { description: ok } }
            """));

        var post = plugin.Validate(Call("POST", "https://api.fabrikam.com/users").Session);
        var delete = plugin.Validate(Call("DELETE", "https://api.fabrikam.com/users").Session);

        Assert.Equal("POST /users", post?.Operation);
        Assert.EndsWith("write.yaml", post!.SpecFile, StringComparison.Ordinal);
        Assert.Empty(post.Issues);
        Assert.Contains("Method DELETE isn't documented", Assert.Single(Messages(delete)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task MatchesPathAndOperationLevelServers()
    {
        var plugin = await CreatePluginWithSpecsAsync(("servers.yaml", """
            openapi: 3.0.3
            info: { title: Servers, version: '1.0' }
            servers:
              - url: https://api.fabrikam.com
            paths:
              /users:
                get:
                  responses: { '204': { description: ok } }
                post:
                  servers:
                    - url: https://write.fabrikam.com
                  responses: { '204': { description: ok } }
              /files:
                servers:
                  - url: https://files.fabrikam.com
                get:
                  responses: { '204': { description: ok } }
            """));

        Assert.Empty(plugin.Validate(Call("GET", "https://api.fabrikam.com/users").Session)!.Issues);
        Assert.Empty(plugin.Validate(Call("POST", "https://write.fabrikam.com/users").Session)!.Issues);
        Assert.Empty(plugin.Validate(Call("GET", "https://files.fabrikam.com/files").Session)!.Issues);
        Assert.Contains("Method POST isn't documented", Assert.Single(Messages(plugin.Validate(Call("POST", "https://api.fabrikam.com/users").Session))), StringComparison.Ordinal);
        Assert.Contains("Path '/files' isn't documented", Assert.Single(Messages(plugin.Validate(Call("GET", "https://api.fabrikam.com/files").Session))), StringComparison.Ordinal);
    }

    [Fact]
    public async Task MatchesPathsCaseSensitivelyAndStrictlyOnTrailingSlash()
    {
        var plugin = await CreatePluginAsync();

        Assert.Null(plugin.Validate(Get("https://API.contoso.com/V1/users/me", HttpStatusCode.OK, "{}").Session));
        Assert.Equal("GET /users/me", plugin.Validate(Get("https://API.contoso.com/v1/users/me", HttpStatusCode.OK, """{ "id": 1, "name": "Alex" }""").Session)?.Operation);
        Assert.Contains("Path '/Users/me' isn't documented", Assert.Single(Messages(plugin.Validate(Get("https://api.contoso.com/v1/Users/me", HttpStatusCode.OK, "{}").Session))), StringComparison.Ordinal);
        Assert.Contains("Path '/users/me/' isn't documented", Assert.Single(Messages(plugin.Validate(Get("https://api.contoso.com/v1/users/me/", HttpStatusCode.OK, "{}").Session))), StringComparison.Ordinal);
    }

    [Fact]
    public async Task DecodesParametersAccordingToStyleAndExplode()
    {
        var plugin = await CreatePluginWithSpecsAsync(("styles.yaml", """
            openapi: 3.0.3
            info: { title: Styles, version: '1.0' }
            servers:
              - url: https://api.fabrikam.com
            paths:
              /items/{ids}:
                get:
                  parameters:
                    - { name: ids, in: path, required: true, style: label, schema: { type: array, items: { type: integer } } }
                    - { name: form, in: query, schema: { type: array, items: { type: integer } } }
                    - { name: csv, in: query, explode: false, schema: { type: array, items: { type: integer } } }
                    - { name: pipes, in: query, style: pipeDelimited, explode: false, schema: { type: array, items: { type: integer } } }
                    - { name: x-ids, in: header, schema: { type: array, items: { type: integer } } }
                    - { name: filter, in: query, required: true, style: deepObject, schema: { type: object } }
                    - { name: score, in: query, schema: { type: number } }
                  responses: { '204': { description: ok } }
            """));

        var valid = plugin.Validate(Call("GET",
            "https://api.fabrikam.com/items/.1,2?form=1&form=2&csv=1,2&pipes=1%7C2&filter[name]=Alex&score=1.5",
            [("x-ids", "1, 2")]).Session);
        var invalid = plugin.Validate(Call("GET",
            "https://api.fabrikam.com/items/.1,2?form=1,2&filter[name]=Alex&score=NaN").Session);

        Assert.Empty(valid!.Issues);
        var messages = Messages(invalid);
        Assert.Contains(messages, m => m.StartsWith("form[0]:", StringComparison.Ordinal) && m.Contains("Expected integer", StringComparison.Ordinal));
        Assert.Contains(messages, m => m.StartsWith("score:", StringComparison.Ordinal) && m.Contains("Expected number", StringComparison.Ordinal));
        Assert.Equal(2, messages.Length);
    }

    [Fact]
    public async Task HandlesObjectQueryParameters()
    {
        var plugin = await CreatePluginWithSpecsAsync(("objects.yaml", """
            openapi: 3.0.3
            info: { title: Objects, version: '1.0' }
            servers:
              - url: https://api.fabrikam.com
            paths:
              /items:
                get:
                  parameters:
                    - name: filter
                      in: query
                      required: true
                      schema: { type: object, properties: { status: { type: string }, owner: { type: string } } }
                    - { name: top, in: query, schema: { type: integer } }
                  responses: { '204': { description: ok } }
            """));

        var exploded = plugin.Validate(Call("GET", "https://api.fabrikam.com/items?status=active&owner=me&top=5").Session);
        var bracketed = plugin.Validate(Call("GET", "https://api.fabrikam.com/items?status=active&top[foo]=bar").Session);
        var missing = plugin.Validate(Call("GET", "https://api.fabrikam.com/items?top=5").Session);

        Assert.Empty(exploded!.Issues);
        Assert.Equal("top[foo]: Query parameter 'top[foo]' isn't documented", Assert.Single(Messages(bracketed)));
        Assert.Equal("filter: Missing required query parameter 'filter'", Assert.Single(Messages(missing)));
    }

    [Fact]
    public async Task CoercesParametersUsingComposedSchemaTypes()
    {
        var plugin = await CreatePluginWithSpecsAsync(("composed.yaml", """
            openapi: 3.0.3
            info: { title: Composed, version: '1.0' }
            servers:
              - url: https://api.fabrikam.com
            paths:
              /items:
                get:
                  parameters:
                    - { name: count, in: query, schema: { allOf: [ { type: integer } ] } }
                    - { name: ids, in: query, schema: { allOf: [ { type: array, items: { allOf: [ { type: integer } ] } } ] } }
                  responses: { '204': { description: ok } }
            """));

        var valid = plugin.Validate(Call("GET", "https://api.fabrikam.com/items?count=42&ids=1&ids=2").Session);
        var invalid = plugin.Validate(Call("GET", "https://api.fabrikam.com/items?count=abc").Session);

        Assert.Empty(valid!.Issues);
        Assert.Contains("Expected integer", Assert.Single(Messages(invalid)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task InspectsRepeatedParameterSchemaReferencesEfficiently()
    {
        var schemas = string.Join(",\n", Enumerable.Range(0, 30).Select(i =>
            $$"""      "S{{i}}": { "allOf": [ { "$ref": "#/components/schemas/S{{i + 1}}" }, { "$ref": "#/components/schemas/S{{i + 1}}" } ] }"""));
        var plugin = await CreatePluginWithSpecsAsync(("wideparams.json", $$"""
            {
              "openapi": "3.1.0",
              "info": { "title": "Wide", "version": "1.0" },
              "servers": [ { "url": "https://api.fabrikam.com" } ],
              "paths": {
                "/wide": {
                  "get": {
                    "parameters": [
                      { "name": "count", "in": "query", "schema": { "$ref": "#/components/schemas/S0" } },
                      { "name": "ids", "in": "query", "schema": { "type": "array", "allOf": [ { "$ref": "#/components/schemas/S0" } ] } },
                      { "name": "filter", "in": "query", "schema": { "type": "object", "allOf": [ { "$ref": "#/components/schemas/S0" } ] } }
                    ],
                    "responses": { "204": { "description": "ok" } }
                  }
                }
              },
              "components": {
                "schemas": {
            {{schemas}},
                  "S30": { "properties": { "status": { "type": "string" } } }
                }
              }
            }
            """));

        var result = plugin.Validate(Call("GET", "https://api.fabrikam.com/wide?count=1&ids=1&status=active").Session);

        Assert.Empty(result!.Issues);
    }

    [Fact]
    public async Task AcceptsParameterValuesValidAsStrings()
    {
        var plugin = await CreatePluginWithSpecsAsync(("strings.yaml", """
            openapi: 3.0.3
            info: { title: Strings, version: '1.0' }
            servers:
              - url: https://api.fabrikam.com
            paths:
              /items:
                get:
                  parameters:
                    - { name: x, in: query, schema: { anyOf: [ { type: integer, enum: [1, 2] }, { type: string } ] } }
                    - { name: n, in: query, schema: { type: integer, enum: [1, 2] } }
                  responses: { '204': { description: ok } }
            """));

        var valid = plugin.Validate(Call("GET", "https://api.fabrikam.com/items?x=42&n=1").Session);
        var invalid = plugin.Validate(Call("GET", "https://api.fabrikam.com/items?n=42").Session);

        Assert.Empty(valid!.Issues);
        Assert.Contains("isn't one of the allowed values", Assert.Single(Messages(invalid)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReportsInvalidJsonForMediaTypesWithoutSchema()
    {
        var plugin = await CreatePluginWithSpecsAsync(("noschema.yaml", """
            openapi: 3.0.3
            info: { title: No schema, version: '1.0' }
            servers:
              - url: https://api.fabrikam.com
            paths:
              /items:
                get:
                  responses:
                    '200': { description: ok, content: { application/json: {} } }
            """));

        var valid = plugin.Validate(TestExchange.Request("GET", "https://api.fabrikam.com/items")
            .WithResponse(HttpStatusCode.OK, headers: [("Content-Type", "application/json")], body: "{}").Session);
        var invalid = plugin.Validate(TestExchange.Request("GET", "https://api.fabrikam.com/items")
            .WithResponse(HttpStatusCode.OK, headers: [("Content-Type", "application/json")], body: "{").Session);

        Assert.Empty(valid!.Issues);
        Assert.Contains("isn't valid JSON", Assert.Single(Messages(invalid)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task MatchesServersWithExplicitDefaultPorts()
    {
        var plugin = await CreatePluginWithSpecsAsync(("ports.yaml", """
            openapi: 3.0.3
            info: { title: Ports, version: '1.0' }
            servers:
              - url: https://api.contoso.com:443/v1
              - url: http://api.contoso.com:80/v1
            paths:
              /items:
                get:
                  responses: { '204': { description: ok } }
            """));

        Assert.NotNull(plugin.Validate(Call("GET", "https://api.contoso.com/v1/items").Session));
        Assert.NotNull(plugin.Validate(Call("GET", "http://api.contoso.com/v1/items").Session));
    }

    [Fact]
    public async Task DistinguishesDottedPropertyNamesFromNestedProperties()
    {
        var plugin = await CreatePluginWithSpecsAsync(("dotted.yaml", """
            openapi: 3.0.3
            info: { title: Dotted, version: '1.0' }
            servers:
              - url: https://api.fabrikam.com
            paths:
              /items:
                get:
                  responses:
                    '200':
                      description: ok
                      content:
                        application/json:
                          schema:
                            type: object
                            properties:
                              a: { type: object, properties: { b: { allOf: [ { $ref: '#/components/schemas/Str' } ] } } }
                              a.b: { allOf: [ { $ref: '#/components/schemas/Str' } ] }
            components:
              schemas:
                Str: { type: string }
            """));

        var result = plugin.Validate(TestExchange.Request("GET", "https://api.fabrikam.com/items")
            .WithResponse(HttpStatusCode.OK, headers: [("Content-Type", "application/json")], body: """{"a":{"b":"ok"},"a.b":123}""").Session);

        Assert.Equal("$[\"a.b\"]: Expected string but got integer", Assert.Single(Messages(result)));
    }

    [Fact]
    public async Task HintsWhenAllOfBranchRejectsPropertyDeclaredInSibling()
    {
        var plugin = await CreatePluginWithSpecsAsync(("allof.yaml", """
            openapi: 3.0.3
            info: { title: AllOf, version: '1.0' }
            servers:
              - url: https://api.fabrikam.com
            paths:
              /items:
                get:
                  responses:
                    '200':
                      description: ok
                      content:
                        application/json:
                          schema:
                            allOf:
                              - { type: object, properties: { id: { type: integer } }, additionalProperties: false }
                              - { type: object, properties: { name: { type: string } } }
            """));

        var result = plugin.Validate(TestExchange.Request("GET", "https://api.fabrikam.com/items")
            .WithResponse(HttpStatusCode.OK, headers: [("Content-Type", "application/json")], body: """{"id":1,"name":"Alex","other":true}""").Session);

        var messages = Messages(result);
        Assert.Equal(2, messages.Length);
        Assert.Contains("$.name: Property 'name' isn't defined in the schema. The property is declared in a sibling allOf schema; additionalProperties: false applies per subschema", messages);
        Assert.Contains("$.other: Property 'other' isn't defined in the schema", messages);
    }

    [Fact]
    public async Task SkipsValidatingLargeBodies()
    {
        var plugin = await CreatePluginWithSpecsAsync(("large.yaml", """
            openapi: 3.0.3
            info: { title: Large, version: '1.0' }
            servers:
              - url: https://api.fabrikam.com
            paths:
              /items:
                get:
                  responses:
                    '200': { description: ok, content: { application/json: { schema: { type: integer } } } }
            """));

        var large = TestExchange.Request("GET", "https://api.fabrikam.com/items")
            .WithResponse(HttpStatusCode.OK, headers: [("Content-Type", "application/json")],
                body: $"\"{new string('a', 5 * 1024 * 1024)}\"").Session;
        var small = TestExchange.Request("GET", "https://api.fabrikam.com/items")
            .WithResponse(HttpStatusCode.OK, headers: [("Content-Type", "application/json")], body: "\"a\"").Session;

        Assert.Empty(plugin.Validate(large)!.Issues);
        Assert.Contains("exceeds the maximum of 5,242,880 bytes", Assert.Single(plugin.GetNotes(large)), StringComparison.Ordinal);
        Assert.Contains("Expected integer", Assert.Single(Messages(plugin.Validate(small))), StringComparison.Ordinal);
        Assert.Empty(plugin.GetNotes(small));
    }

    [Fact]
    public async Task DoesNotReuseDepthLimitedResultsAtShallowerDepths()
    {
        // the chain reaches Str exactly beyond the depth limit, while the
        // second anyOf branch reaches it at a shallow depth
        var schemas = string.Join(",\n", Enumerable.Range(0, 63).Select(i =>
            $$"""      "S{{i}}": { "allOf": [ { "$ref": "#/components/schemas/S{{i + 1}}" } ] }"""));
        var plugin = await CreatePluginWithSpecsAsync(("depthcache.json", $$"""
            {
              "openapi": "3.0.3",
              "info": { "title": "Depth cache", "version": "1.0" },
              "servers": [ { "url": "https://api.fabrikam.com" } ],
              "paths": {
                "/items": {
                  "get": {
                    "responses": {
                      "200": { "description": "ok", "content": { "application/json": { "schema": {
                        "anyOf": [ { "$ref": "#/components/schemas/S0" }, { "$ref": "#/components/schemas/Str" } ]
                      } } } }
                    }
                  }
                }
              },
              "components": {
                "schemas": {
            {{schemas}},
                  "S63": { "allOf": [ { "$ref": "#/components/schemas/Str" } ] },
                  "Str": { "type": "string" }
                }
              }
            }
            """));

        var result = plugin.Validate(TestExchange.Request("GET", "https://api.fabrikam.com/items")
            .WithResponse(HttpStatusCode.OK, headers: [("Content-Type", "application/json")], body: "\"ok\"").Session);

        Assert.Empty(result!.Issues);
    }

    [Fact]
    public async Task ReportsMethodNotDocumentedOnMoreSpecificLiteralPath()
    {
        var plugin = await CreatePluginWithSpecsAsync(("literal.yaml", """
            openapi: 3.0.3
            info: { title: Literal, version: '1.0' }
            servers:
              - url: https://api.fabrikam.com
            paths:
              /users/me:
                get:
                  responses: { '204': { description: ok } }
              /users/{id}:
                post:
                  parameters:
                    - { name: id, in: path, required: true, schema: { type: string } }
                  responses: { '204': { description: ok } }
            """));

        var result = plugin.Validate(Call("POST", "https://api.fabrikam.com/users/me").Session);

        Assert.Equal("Method POST isn't documented for path '/users/me'", Assert.Single(Messages(result)));
    }

    [Fact]
    public async Task RejectsNullNotInEnumForOpenApi31TypeArrays()
    {
        var plugin = await CreatePluginWithSpecsAsync(("nullenum.yaml", """
            openapi: 3.1.0
            info: { title: Null enum, version: '1.0' }
            servers:
              - url: https://api.fabrikam.com
            paths:
              /items:
                get:
                  responses:
                    '200':
                      description: ok
                      content:
                        application/json:
                          schema: { type: [string, 'null'], enum: [active, inactive] }
            """));

        var result = plugin.Validate(TestExchange.Request("GET", "https://api.fabrikam.com/items")
            .WithResponse(HttpStatusCode.OK, headers: [("Content-Type", "application/json")], body: "null").Session);

        Assert.Contains("isn't one of the allowed values", Assert.Single(Messages(result)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExemptsReadOnlyPropertiesDeclaredInComposedSchemas()
    {
        var plugin = await CreatePluginWithSpecsAsync(("readonly.yaml", """
            openapi: 3.0.3
            info: { title: Read-only, version: '1.0' }
            servers:
              - url: https://api.fabrikam.com
            paths:
              /items:
                post:
                  requestBody:
                    content:
                      application/json:
                        schema:
                          required: [id, name]
                          allOf:
                            - $ref: '#/components/schemas/Base'
                            - { type: object, properties: { name: { type: string } } }
                  responses: { '204': { description: ok } }
            components:
              schemas:
                Base: { type: object, properties: { id: { type: integer, readOnly: true } } }
            """));

        var valid = plugin.Validate(TestExchange.Request("POST", "https://api.fabrikam.com/items",
            headers: [("Content-Type", "application/json")], body: """{"name":"Alex"}""").WithResponse(HttpStatusCode.NoContent).Session);
        var invalid = plugin.Validate(TestExchange.Request("POST", "https://api.fabrikam.com/items",
            headers: [("Content-Type", "application/json")], body: """{"id":1}""").WithResponse(HttpStatusCode.NoContent).Session);

        Assert.Empty(valid!.Issues);
        Assert.Equal("$: Missing required property 'name'", Assert.Single(Messages(invalid)));
    }

    [Fact]
    public async Task ReportsSchemasExceedingMaxDepth()
    {
        var schemas = string.Join(",\n", Enumerable.Range(0, 70).Select(i =>
            $$"""      "S{{i}}": { "allOf": [ { "$ref": "#/components/schemas/S{{i + 1}}" } ] }"""));
        var plugin = await CreatePluginWithSpecsAsync(("deep.json", $$"""
            {
              "openapi": "3.0.3",
              "info": { "title": "Deep", "version": "1.0" },
              "servers": [ { "url": "https://api.fabrikam.com" } ],
              "paths": {
                "/deep": {
                  "get": {
                    "responses": {
                      "200": { "description": "ok", "content": { "application/json": { "schema": { "$ref": "#/components/schemas/S0" } } } }
                    }
                  }
                }
              },
              "components": {
                "schemas": {
            {{schemas}},
                  "S70": { "type": "string" }
                }
              }
            }
            """));

        var result = plugin.Validate(TestExchange.Request("GET", "https://api.fabrikam.com/deep")
            .WithResponse(HttpStatusCode.OK, headers: [("Content-Type", "application/json")], body: "1").Session);

        Assert.Contains(Messages(result), m => m.Contains("supported maximum", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ValidatesRepeatedSchemaReferencesEfficiently()
    {
        var schemas = string.Join(",\n", Enumerable.Range(0, 30).Select(i =>
            $$"""      "S{{i}}": { "allOf": [ { "$ref": "#/components/schemas/S{{i + 1}}" }, { "$ref": "#/components/schemas/S{{i + 1}}" } ] }"""));
        var plugin = await CreatePluginWithSpecsAsync(("wide.json", $$"""
            {
              "openapi": "3.0.3",
              "info": { "title": "Wide", "version": "1.0" },
              "servers": [ { "url": "https://api.fabrikam.com" } ],
              "paths": {
                "/wide": {
                  "get": {
                    "responses": {
                      "200": { "description": "ok", "content": { "application/json": { "schema": { "$ref": "#/components/schemas/S0" } } } }
                    }
                  }
                }
              },
              "components": {
                "schemas": {
            {{schemas}},
                  "S30": { "type": "string" }
                }
              }
            }
            """));

        var valid = plugin.Validate(TestExchange.Request("GET", "https://api.fabrikam.com/wide")
            .WithResponse(HttpStatusCode.OK, headers: [("Content-Type", "application/json")], body: "\"ok\"").Session);
        var invalid = plugin.Validate(TestExchange.Request("GET", "https://api.fabrikam.com/wide")
            .WithResponse(HttpStatusCode.OK, headers: [("Content-Type", "application/json")], body: "1").Session);

        Assert.Empty(valid!.Issues);
        Assert.Equal("$: Expected string but got integer", Assert.Single(Messages(invalid)));
    }

    [Fact]
    public async Task ValidatesLargeInvalidArraysInLinearTime()
    {
        var plugin = await CreatePluginWithSpecsAsync(("array.yaml", """
            openapi: 3.0.3
            info: { title: Array, version: "1.0" }
            servers:
              - url: https://api.fabrikam.com
            paths:
              /items:
                get:
                  responses:
                    "200":
                      description: ok
                      content:
                        application/json:
                          schema:
                            type: array
                            items:
                              allOf:
                                - type: string
            """));
        const int count = 20_000;
        var body = $"[{string.Join(',', Enumerable.Repeat("1", count))}]";

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var result = plugin.Validate(TestExchange.Request("GET", "https://api.fabrikam.com/items")
            .WithResponse(HttpStatusCode.OK, headers: [("Content-Type", "application/json")], body: body).Session);
        stopwatch.Stop();

        Assert.Equal(count, result!.Issues.Count());
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5), $"Validation took {stopwatch.Elapsed}");
    }

    [Fact]
    public async Task WarnsAboutSpecErrors()
    {
        await File.WriteAllTextAsync(Path.Combine(_dir.FullName, "broken.yaml"), """
            openapi: 3.0.3
            info: { title: Broken, version: '1.0' }
            servers:
              - url: https://api.fabrikam.com
            paths:
              /items:
                get:
                  parameters:
                    - { name: id, in: nowhere, schema: { type: integer } }
                  responses: { '204': { description: ok } }
            """);
        var logger = new WarningLogger();

        var plugin = await CreatePluginAsync("""{ "specFiles": [ "broken.yaml" ] }""", logger);

        Assert.NotNull(plugin.Validate(Call("GET", "https://api.fabrikam.com/items").Session));
        Assert.Contains(logger.Warnings, w => w.Contains("broken.yaml' has errors", StringComparison.Ordinal));
    }

    private sealed class WarningLogger : ILogger<OpenApiValidationPlugin>
    {
        public List<string> Warnings { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
            {
                Warnings.Add(formatter(state, exception));
            }
        }
    }
}
