// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Net;
using System.Text.Json.Nodes;
using DevProxy.Abstractions.LanguageModel;
using DevProxy.Plugins.Mocking;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.OpenApi;
using Microsoft.OpenApi.Reader;
using Xunit;

namespace DevProxy.Integration.Tests;

/// <summary>
/// Coverage for <see cref="OpenApiMockResponsePlugin"/> served through the Kestrel engine
/// (with the language model disabled, so responses come from spec examples or the
/// schema-based fallback) and for <see cref="OpenApiSchemaSampleGenerator"/>.
/// </summary>
public sealed class OpenApiMockResponsePluginTests
{
    [Fact]
    public async Task TextResponseWithStringSchema_IsMocked()
    {
        var (status, body, received) = await GetAsync("/text-string", """
            content:
              text/plain:
                schema: { type: string }
            """);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("string", body);
        Assert.False(received);
    }

    [Fact]
    public async Task TextResponseWithObjectSchema_PassesThrough()
    {
        var (_, _, received) = await GetAsync("/text-object", """
            content:
              text/plain:
                schema: { type: object, properties: { a: { type: string } } }
            """);

        Assert.True(received);
    }

    [Fact]
    public async Task TextResponseWithNonStringExample_PassesThrough()
    {
        var (_, _, received) = await GetAsync("/text-number", """
            content:
              text/plain:
                example: 42
            """);

        Assert.True(received);
    }

    [Fact]
    public async Task XmlResponseWithStringExample_IsMocked()
    {
        var (status, body, received) = await GetAsync("/xml", """
            content:
              application/xml:
                example: '<a>1</a>'
            """);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("<a>1</a>", body);
        Assert.False(received);
    }

    [Fact]
    public async Task JsonResponseWithObjectSchema_IsMocked()
    {
        var (status, body, received) = await GetAsync("/json", """
            content:
              application/json:
                schema: { type: object, properties: { name: { type: string } } }
            """);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("string", JsonNode.Parse(body)!["name"]!.GetValue<string>());
        Assert.False(received);
    }

    [Fact]
    public async Task ResponseExceedingSampleSize_PassesThrough()
    {
        var (_, _, received) = await GetAsync("/huge", """
            content:
              application/json:
                schema: { type: array, minItems: 2147483647, items: { type: integer } }
            """);

        Assert.True(received);
    }

    [Theory]
    [InlineData("{ type: array, minItems: 2147483647, items: { type: integer } }")]
    [InlineData("{ type: string, minLength: 2147483647 }")]
    [InlineData("{ type: array, minItems: 1000, items: { type: array, minItems: 1000, items: { type: integer } } }")]
    [InlineData("{ type: array, minItems: 1000, items: { type: object, properties: { a: { type: array, minItems: 1000, items: { type: integer } } } } }")]
    // one node over the limit: the array plus 100000 items
    [InlineData("{ type: array, minItems: 100000, items: { type: integer } }")]
    // one over the limit: the string node plus 100000 characters
    [InlineData("{ type: string, minLength: 100000 }")]
    // over the limit: the array plus 11112 strings of 1 node and 8 characters
    [InlineData("{ type: array, minItems: 11112, items: { type: string, minLength: 8 } }")]
    // over the limit: the array plus 14286 "string" values of 1 node and 6 characters
    [InlineData("{ type: array, minItems: 14286, items: { type: string } }")]
    // over the limit: unique variants grow, eg. "string10999"
    [InlineData("{ type: array, minItems: 11000, uniqueItems: true, items: { type: string } }")]
    [InlineData("{ type: array, minItems: 11000, uniqueItems: true, items: { type: object, properties: { a: { type: string } } } }")]
    public void TryGenerateSample_DeclinesSamplesExceedingMaxSize(string schema)
    {
        Assert.False(OpenApiSchemaSampleGenerator.TryGenerateSample(ParseSchema(schema), out var sample));
        Assert.Null(sample);
    }

    [Fact]
    public void TryGenerateSample_DeclinesUniqueEnumVariantsExceedingMaxSize()
    {
        var schema = new OpenApiSchema
        {
            Type = JsonSchemaType.Array,
            MinItems = 2,
            UniqueItems = true,
            Items = new OpenApiSchema
            {
                Type = JsonSchemaType.String,
                Enum = [JsonValue.Create("a"), JsonValue.Create(new string('x', 100_000))]
            }
        };

        Assert.False(OpenApiSchemaSampleGenerator.TryGenerateSample(schema, out var sample));
        Assert.Null(sample);
    }

    [Fact]
    public async Task UniqueItemsExceedingSampleSize_PassesThrough()
    {
        var (_, _, received) = await GetAsync("/unique-growing", """
            content:
              application/json:
                schema: { type: array, minItems: 11000, uniqueItems: true, items: { type: string } }
            """);

        Assert.True(received);
    }

    [Fact]
    public void ExponentialAllOfComposition_IsDeclined()
    {
        // each level references the next one ten times, which expands to
        // billions of schemas when flattened
        var levels = Enumerable.Range(0, 10).Select(i => i == 9 ?
            $"    L{i}: {{ type: integer }}" :
            $"    L{i}: {{ allOf: [{string.Join(", ", Enumerable.Repeat($"{{ $ref: '#/components/schemas/L{i + 1}' }}", 10))}] }}");
        var spec = string.Join('\n', [
            "openapi: 3.0.1",
            "info: { title: Test, version: '1.0' }",
            "paths: {}",
            "components:",
            "  schemas:",
            .. levels
        ]);
        var settings = new OpenApiReaderSettings();
        settings.AddYamlReader();
        var schema = OpenApiDocument.Parse(spec, null, settings).Document!.Components!.Schemas!["L0"];

        Assert.False(OpenApiSchemaSampleGenerator.TryGenerateSample(schema, out var sample));
        Assert.Null(sample);
        Assert.False(OpenApiSchemaSampleGenerator.TryDescribe(schema, out _));
        Assert.False(OpenApiSchemaSampleGenerator.IsStringSchema(schema));
    }

    [Theory]
    [InlineData("{ allOf: [ { type: array, minItems: 60000, items: { type: integer } } ] }", 60000)]
    [InlineData("{ type: array, minItems: 60000, uniqueItems: true, items: { type: integer } }", 60000)]
    // exactly at the limit: the array plus 99999 items
    [InlineData("{ type: array, minItems: 99999, items: { type: integer } }", 99999)]
    // exactly at the limit: the array plus 11111 strings of 1 node and 8 characters
    [InlineData("{ type: array, minItems: 11111, items: { type: string, minLength: 8 } }", 11111)]
    public void TryGenerateSample_GeneratesLargeSamplesWithinMaxSize(string schema, int expectedCount)
    {
        Assert.Equal(expectedCount, Assert.IsType<JsonArray>(Generate(ParseSchema(schema))).Count);
    }

    [Theory]
    // exactly at the limit: the array plus 99999 nulls
    [InlineData(99999, true)]
    // one node over the limit: the array plus 100000 nulls
    [InlineData(100000, false)]
    public void TryGenerateSample_CountsNullItemsTowardsMaxSize(int minItems, bool expected)
    {
        var schema = ParseSchema($"{{ type: array, minItems: {minItems}, items: {{ type: 'null' }} }}", "3.1.0");

        Assert.Equal(expected, OpenApiSchemaSampleGenerator.TryGenerateSample(schema, out var sample));
        Assert.Equal(expected ? minItems : null, (sample as JsonArray)?.Count);
    }

    [Fact]
    public void TryGenerateSample_GeneratesOverlappingAllOfPropertyOnce()
    {
        // each branch's value alone takes 40001 units, so generating them
        // before intersecting them would exceed the limit
        var sample = Generate(ParseSchema("""
            allOf:
              - { type: object, properties: { value: { type: string, minLength: 40000 } } }
              - { type: object, properties: { value: { type: string, minLength: 40000 } } }
            """));

        Assert.Equal(40000, sample!["value"]!.GetValue<string>().Length);
    }

    [Fact]
    public void TryGenerateSample_DoesntGenerateItemsForEmptyArray()
    {
        var sample = Generate(ParseSchema("{ type: array, maxItems: 0, items: { type: string, minLength: 100000 } }"));

        Assert.Empty(Assert.IsType<JsonArray>(sample));
    }

    [Fact]
    public void TryGenerateSample_GeneratesStringAtMaxSize()
    {
        // the string node plus 99999 characters
        Assert.Equal(99999, Generate(ParseSchema("{ type: string, minLength: 99999 }"))!.GetValue<string>().Length);
    }

    [Theory]
    [InlineData("{ type: integer, minimum: 0, exclusiveMinimum: true }", "1")]
    [InlineData("{ type: integer, maximum: 0, exclusiveMaximum: true }", "-1")]
    [InlineData("{ type: integer, minimum: 2.5, exclusiveMinimum: true }", "3")]
    [InlineData("{ type: integer, minimum: 0, exclusiveMinimum: true, multipleOf: 5 }", "5")]
    [InlineData("{ type: integer, minimum: 10, exclusiveMinimum: true, multipleOf: 5 }", "15")]
    [InlineData("{ type: integer, maximum: 0, exclusiveMaximum: true, multipleOf: 5 }", "-5")]
    [InlineData("{ type: number, minimum: 0, exclusiveMinimum: true, maximum: 0.5 }", "0.25")]
    [InlineData("{ type: number, minimum: 0, exclusiveMinimum: true, multipleOf: 0.25 }", "0.25")]
    [InlineData("{ allOf: [ { type: integer, minimum: 3 }, { minimum: 3, exclusiveMinimum: true } ] }", "4")]
    [InlineData("{ allOf: [ { type: integer, minimum: 3, exclusiveMinimum: true }, { minimum: 3 } ] }", "4")]
    public void GenerateSample_HonorsExclusiveBounds(string schema, string expected)
    {
        var sample = Generate(ParseSchema(schema));

        Assert.Equal(decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), sample!.GetValue<decimal>());
    }

    [Theory]
    [InlineData("{ allOf: [ { type: string, enum: [a, b] }, { type: string, enum: [b, c] } ] }", "\"b\"")]
    [InlineData("{ allOf: [ { type: array, items: { type: integer, maximum: 10 } }, { type: array, items: { type: integer, minimum: 5 } } ] }", "[5]")]
    [InlineData("{ allOf: [ { oneOf: [ { type: integer } ] } ] }", "0")]
    [InlineData("{ allOf: [ { anyOf: [ { type: integer, minimum: 3 } ] }, { maximum: 5 } ] }", "3")]
    [InlineData("{ type: integer, minimum: 10, oneOf: [ { type: integer } ] }", "10")]
    [InlineData("{ type: array, minItems: 2, anyOf: [ { type: array, items: { type: integer } } ] }", "[0,0]")]
    [InlineData("{ allOf: [ { type: number, minimum: 0.5 }, { type: integer } ] }", "1")]
    [InlineData("{ allOf: [ { type: integer }, { type: number, minimum: 0.5 } ] }", "1")]
    [InlineData("{ properties: { id: { type: integer } }, oneOf: [ { properties: { name: { type: string } } } ] }", "{\"name\":\"string\",\"id\":0}")]
    public void GenerateSample_CombinesAllOfConstraints(string schema, string expected)
    {
        var sample = Generate(ParseSchema(schema));

        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(expected), sample), sample?.ToJsonString());
    }

    [Theory]
    [InlineData("{ type: array, minItems: 3, items: { type: integer } }", 3)]
    [InlineData("{ type: array, items: { type: integer } }", 1)]
    [InlineData("{ type: array, minItems: 5, maxItems: 2, items: { type: integer } }", 2)]
    [InlineData("{ type: array, maxItems: 0, items: { type: integer } }", 0)]
    [InlineData("{ allOf: [ { type: array, minItems: 3, items: { type: integer } } ] }", 3)]
    [InlineData("{ allOf: [ { type: array, minItems: 5, items: { type: integer } }, { maxItems: 2 } ] }", 2)]
    public void GenerateSample_HonorsMinItems(string schema, int expectedCount)
    {
        var sample = Generate(ParseSchema(schema));

        Assert.Equal(expectedCount, Assert.IsType<JsonArray>(sample).Count);
    }

    [Theory]
    [InlineData("{ type: integer, minimum: 7, multipleOf: 5 }", "10")]
    [InlineData("{ type: integer, maximum: -7, multipleOf: 5 }", "-10")]
    [InlineData("{ type: number, minimum: 0.3, multipleOf: 0.25 }", "0.5")]
    [InlineData("{ type: integer, minimum: 1, multipleOf: 0.5 }", "1")]
    [InlineData("{ type: integer, minimum: 1.2, multipleOf: 1.5 }", "3")]
    [InlineData("{ type: integer, multipleOf: 3 }", "0")]
    [InlineData("{ type: integer, minimum: 1.2, multipleOf: 0.0000000013 }", "13")]
    [InlineData("{ allOf: [ { type: integer, minimum: 7, multipleOf: 5 } ] }", "10")]
    [InlineData("{ allOf: [ { type: integer, multipleOf: 4 }, { minimum: 1, multipleOf: 6 } ] }", "12")]
    public void GenerateSample_HonorsMultipleOf(string schema, string expected)
    {
        var sample = Generate(ParseSchema(schema));

        Assert.Equal(decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), sample!.GetValue<decimal>());
    }

    [Theory]
    [InlineData("{ type: array, minItems: 3, uniqueItems: true, items: { type: integer } }", "[0,1,2]")]
    [InlineData("{ type: array, minItems: 3, uniqueItems: true, items: { type: integer, maximum: -10, multipleOf: 5 } }", "[-10,-15,-20]")]
    [InlineData("{ type: array, minItems: 2, uniqueItems: true, items: { type: string, maxLength: 6 } }", "[\"string\",\"strin1\"]")]
    [InlineData("{ type: array, minItems: 2, uniqueItems: true, items: { type: string, format: email } }", "[\"user@example.com\",\"user1@example.com\"]")]
    [InlineData("{ type: array, minItems: 3, uniqueItems: true, items: { type: boolean } }", "[true,false]")]
    [InlineData("{ type: array, minItems: 3, uniqueItems: true, items: { type: string, enum: [a, b] } }", "[\"a\",\"b\"]")]
    [InlineData("{ type: array, minItems: 3, uniqueItems: true, items: { type: string, format: date } }", "[\"2024-01-01\"]")]
    [InlineData("{ type: array, minItems: 2, uniqueItems: true, items: { type: object, properties: { id: { type: integer }, name: { type: string } } } }", "[{\"id\":0,\"name\":\"string\"},{\"id\":1,\"name\":\"string\"}]")]
    [InlineData("{ type: array, minItems: 2, items: { type: integer } }", "[0,0]")]
    [InlineData("{ type: array, maxItems: 0, uniqueItems: true, items: { type: integer } }", "[]")]
    public void GenerateSample_HonorsUniqueItems(string schema, string expected)
    {
        var sample = Generate(ParseSchema(schema));

        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(expected), sample), sample?.ToJsonString());
    }

    [Theory]
    [InlineData("{ allOf: [ { type: object, properties: { id: { type: integer, minimum: 7 } } }, { type: object, properties: { id: { type: integer, maximum: 10 } } } ] }", "{\"id\":7}")]
    [InlineData("{ properties: { id: { type: integer, maximum: -3 } }, allOf: [ { type: object, properties: { id: { type: integer, multipleOf: 5 } } } ] }", "{\"id\":-5}")]
    [InlineData("{ allOf: [ { type: object, properties: { name: { type: string, minLength: 8 } } }, { properties: { name: { type: string, maxLength: 10 }, age: { type: integer } } } ] }", "{\"name\":\"stringxx\",\"age\":0}")]
    public void GenerateSample_IntersectsOverlappingAllOfProperties(string schema, string expected)
    {
        var sample = Generate(ParseSchema(schema));

        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(expected), sample), sample?.ToJsonString());
    }

    [Fact]
    public async Task LanguageModelResponse_IsUsedAndCached()
    {
        var client = new FakeLanguageModelClient("```json\n{ \"name\": \"Megan Bowen\" }\n```");

        var results = await GetAsync("/lm", """
            content:
              application/json:
                schema: { type: object, properties: { name: { type: string } } }
            """, client, requestCount: 2);

        Assert.All(results, r => Assert.Equal("Megan Bowen", JsonNode.Parse(r.Body)!["name"]!.GetValue<string>()));
        Assert.Equal(1, client.CompletionCount);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[1, 2]")]
    [InlineData(null)]
    public async Task InvalidLanguageModelResponse_FallsBackToSchema(string? response)
    {
        var client = new FakeLanguageModelClient(response);

        var results = await GetAsync("/lm-invalid", """
            content:
              application/json:
                schema: { type: object, properties: { name: { type: string } } }
            """, client);

        Assert.Equal("string", JsonNode.Parse(results[0].Body)!["name"]!.GetValue<string>());
        Assert.False(results[0].OriginReceived);
    }

    [Fact]
    public async Task LanguageModelScalarWrapper_IsUnwrapped()
    {
        var client = new FakeLanguageModelClient("{ \"count\": 42 }");

        var results = await GetAsync("/lm-count", """
            content:
              application/json:
                schema: { type: integer }
            """, client);

        Assert.Equal("42", results[0].Body);
    }

    [Fact]
    public async Task ResponseWithExample_DoesNotCallLanguageModel()
    {
        var client = new FakeLanguageModelClient("{ \"name\": \"Megan Bowen\" }");

        var results = await GetAsync("/example", """
            content:
              application/json:
                schema: { type: object, properties: { name: { type: string } } }
                example: { name: Adele Vance }
            """, client);

        Assert.Equal("Adele Vance", JsonNode.Parse(results[0].Body)!["name"]!.GetValue<string>());
        Assert.Equal(0, client.CompletionCount);
    }

    [Fact]
    public void Describe_IncludesConstraints()
    {
        var description = OpenApiSchemaSampleGenerator.Describe(ParseSchema("""
            type: object
            properties:
              code: { type: string, minLength: 2, maxLength: 4, pattern: '^[A-Z]+$' }
              amount: { type: number, multipleOf: 0.5 }
              tags: { type: array, minItems: 1, maxItems: 3, uniqueItems: true, items: { type: string } }
            """));

        var properties = description["properties"]!;
        Assert.Equal(2, properties["code"]!["minLength"]!.GetValue<int>());
        Assert.Equal(4, properties["code"]!["maxLength"]!.GetValue<int>());
        Assert.Equal("^[A-Z]+$", properties["code"]!["pattern"]!.GetValue<string>());
        Assert.Equal(0.5m, properties["amount"]!["multipleOf"]!.GetValue<decimal>());
        Assert.Equal(1, properties["tags"]!["minItems"]!.GetValue<int>());
        Assert.Equal(3, properties["tags"]!["maxItems"]!.GetValue<int>());
        Assert.True(properties["tags"]!["uniqueItems"]!.GetValue<bool>());
    }

    [Fact]
    public void Describe_KeepsStricterAllOfConstraints()
    {
        var description = OpenApiSchemaSampleGenerator.Describe(ParseSchema("""
            allOf:
              - { type: string, minLength: 5, maxLength: 8 }
              - { minLength: 1, maxLength: 20 }
              - { type: array, minItems: 3, maxItems: 4, multipleOf: 4 }
            minItems: 2
            maxItems: 10
            multipleOf: 6
            """));

        Assert.Equal(5, description["minLength"]!.GetValue<int>());
        Assert.Equal(8, description["maxLength"]!.GetValue<int>());
        Assert.Equal(3, description["minItems"]!.GetValue<int>());
        Assert.Equal(4, description["maxItems"]!.GetValue<int>());
        Assert.Equal(12m, description["multipleOf"]!.GetValue<decimal>());
    }

    [Fact]
    public void Describe_MergesAllOfAndSiblingConstraints()
    {
        var description = OpenApiSchemaSampleGenerator.Describe(ParseSchema("""
            allOf:
              - type: object
                properties:
                  id: { type: integer, minimum: 7 }
                  status: { type: string, enum: [a, b] }
                  tags: { type: array, items: { type: integer, maximum: 10 } }
            properties:
              id: { type: integer, maximum: 10, exclusiveMaximum: true }
              status: { type: string, enum: [b, c] }
              tags: { type: array, items: { type: integer, minimum: 5 } }
            """));

        var properties = description["properties"]!;
        Assert.Equal(7m, properties["id"]!["minimum"]!.GetValue<decimal>());
        Assert.Equal(10m, properties["id"]!["exclusiveMaximum"]!.GetValue<decimal>());
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse("[\"b\"]"), properties["status"]!["enum"]));
        Assert.Equal(10m, properties["tags"]!["items"]!["maximum"]!.GetValue<decimal>());
        Assert.Equal(5m, properties["tags"]!["items"]!["minimum"]!.GetValue<decimal>());
    }

    private static JsonNode? Generate(IOpenApiSchema schema)
    {
        Assert.True(OpenApiSchemaSampleGenerator.TryGenerateSample(schema, out var sample));
        return sample;
    }

    private static IOpenApiSchema ParseSchema(string schemaYaml, string openApiVersion = "3.0.1")
    {
        var spec = string.Join('\n', [
            $"openapi: {openApiVersion}",
            "info: { title: Test, version: '1.0' }",
            "paths: {}",
            "components:",
            "  schemas:",
            "    Test:",
            .. Indent(schemaYaml, 6)
        ]);
        var settings = new OpenApiReaderSettings();
        settings.AddYamlReader();
        var document = OpenApiDocument.Parse(spec, null, settings).Document!;
        return document.Components!.Schemas!["Test"];
    }

    private static IEnumerable<string> Indent(string yaml, int spaces) =>
        yaml.Split('\n').Select(l => new string(' ', spaces) + l.TrimEnd('\r'));

    private static async Task<(HttpStatusCode Status, string Body, bool OriginReceived)> GetAsync(string path, string responseYaml) =>
        (await GetAsync(path, responseYaml, new DisabledLanguageModelClient()))[0];

    private static async Task<List<(HttpStatusCode Status, string Body, bool OriginReceived)>> GetAsync(
        string path, string responseYaml, ILanguageModelClient languageModelClient, int requestCount = 1)
    {
        await using var origin = await FakeOrigin.StartAsync();

        var dir = Directory.CreateTempSubdirectory("devproxy-openapimock-");
        try
        {
            var specFile = Path.Combine(dir.FullName, "spec.yaml");
            await File.WriteAllTextAsync(specFile, string.Join('\n', [
                "openapi: 3.0.1",
                "info: { title: Test, version: '1.0' }",
                "servers:",
                $"  - url: http://{origin.Host}",
                "paths:",
                $"  {path}:",
                "    get:",
                "      responses:",
                "        '200':",
                "          description: OK",
                .. Indent(responseYaml, 10)
            ]));

            var proxyConfig = new TestProxyConfiguration
            {
                ConfigFile = Path.Combine(dir.FullName, "devproxyrc.json"),
            };
            var config = PluginConfig.FromJson($$"""{ "specFile": "{{specFile.Replace("\\", "\\\\", StringComparison.Ordinal)}}" }""");

            using var plugin = new OpenApiMockResponsePlugin(
                TestDefaults.HttpClient,
                NullLogger<OpenApiMockResponsePlugin>.Instance,
                KestrelProxyHarness.BuildUrlsToWatch(origin.Host),
                languageModelClient,
                proxyConfig,
                config);

            using var host = new PluginTestHost(proxyConfig);
            await plugin.InitializeAsync(host.CreateInitArgs(), CancellationToken.None);

            await using var proxy = await KestrelProxyHarness.StartAsync(origin.Host, [plugin]);
            using var client = proxy.CreateHttpClient();

            var results = new List<(HttpStatusCode, string, bool)>();
            for (var i = 0; i < requestCount; i++)
            {
                using var httpResponse = await client.GetAsync(new Uri($"http://{origin.Host}{path}"));
                var body = await httpResponse.Content.ReadAsStringAsync();
                results.Add((httpResponse.StatusCode, body, origin.ReceivedRequests.Any(r => r.PathAndQuery == path)));
            }
            return results;
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    private sealed class FakeLanguageModelClient(string? response) : ILanguageModelClient
    {
        private int _completionCount;

        public int CompletionCount => _completionCount;

        public Task<ILanguageModelCompletionResponse?> GenerateChatCompletionAsync(
            string promptFileName, Dictionary<string, object> parameters, CancellationToken cancellationToken)
        {
            _ = Interlocked.Increment(ref _completionCount);
            return Task.FromResult<ILanguageModelCompletionResponse?>(new FakeCompletionResponse(response));
        }

        public Task<ILanguageModelCompletionResponse?> GenerateChatCompletionAsync(
            IEnumerable<ILanguageModelChatCompletionMessage> messages, CompletionOptions? options, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<ILanguageModelCompletionResponse?> GenerateCompletionAsync(
            string prompt, CompletionOptions? options, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<bool> IsEnabledAsync(CancellationToken cancellationToken) => Task.FromResult(true);
    }

    private sealed class FakeCompletionResponse(string? response) : ILanguageModelCompletionResponse
    {
        public string? ErrorMessage => null;
        public string? Response => response;
        public string? RequestUrl { get; set; }
        public OpenAIResponse ConvertToOpenAIResponse() => throw new NotSupportedException();
    }
}
