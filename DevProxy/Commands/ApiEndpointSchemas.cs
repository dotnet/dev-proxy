// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Text.Json.Nodes;

namespace DevProxy.Commands;

/// <summary>
/// Request and response shapes of the Dev Proxy API endpoints exposed by
/// <see cref="ApiControllers.ProxyController"/>. Shapes follow OpenAPI 3.1
/// conventions (JSON Schema for bodies, content maps keyed by media type) so
/// tools and agents can build requests without additional documentation.
/// </summary>
static class ApiEndpointSchemas
{
    internal const string JsonContentType = "application/json";
    internal const string ProblemContentType = "application/problem+json";

    internal sealed class EndpointSchema
    {
        public JsonArray? Query { get; init; }
        public JsonObject? RequestBody { get; init; }
        public required JsonObject Responses { get; init; }
    }

    public static EndpointSchema? Get(string method, string path) => (method.ToUpperInvariant(), path) switch
    {
        ("GET", "/proxy") => new()
        {
            Responses = Responses(
                ("200", Response("Current proxy status", JsonContentType, ProxyInfoSchema())))
        },
        ("POST", "/proxy") => new()
        {
            RequestBody = RequestBody(new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["recording"] = new JsonObject
                    {
                        ["type"] = "boolean",
                        ["description"] = "true to start recording, false to stop recording. Omit to leave unchanged."
                    }
                },
                ["examples"] = new JsonArray(new JsonObject { ["recording"] = true })
            }),
            Responses = Responses(
                ("200", Response("Updated proxy status", JsonContentType, ProxyInfoSchema())),
                ("400", Response("Invalid request, for example when the body is missing or sets configFile", ProblemContentType, ValidationProblemSchema())))
        },
        ("POST", "/proxy/mockRequest") => new()
        {
            Responses = Responses(
                ("202", Response("Mock request issued")))
        },
        ("POST", "/proxy/stopProxy") => new()
        {
            Responses = Responses(
                ("202", Response("Shutdown accepted; Dev Proxy stops asynchronously")))
        },
        ("POST", "/proxy/jwtToken") => new()
        {
            RequestBody = RequestBody(JwtOptionsSchema()),
            Responses = Responses(
                ("200", Response("Generated JWT token", JsonContentType, new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["token"] = new JsonObject { ["type"] = "string" }
                    },
                    ["required"] = new JsonArray("token")
                })),
                ("400", Response("Invalid request, for example when the body is missing or the signing key is too short", ProblemContentType, ValidationProblemSchema())))
        },
        ("GET", "/proxy/rootCertificate") => new()
        {
            Query = new JsonArray(
                QueryParameter("format", "Certificate format. Only crt (PEM-encoded) is supported.", required: true, new JsonObject
                {
                    ["type"] = "string",
                    ["enum"] = new JsonArray("crt")
                })),
            Responses = Responses(
                ("200", Response("PEM-encoded root certificate (devProxy.pem)", "application/x-x509-ca-cert", new JsonObject { ["type"] = "string" })),
                ("400", Response("format is missing or not supported", ProblemContentType, ValidationProblemSchema())))
        },
        ("GET", "/proxy/logs") => new()
        {
            Query = new JsonArray(
                QueryParameter("lines", "Number of most recent log lines to return", required: false, new JsonObject
                {
                    ["type"] = "integer",
                    ["default"] = 50
                }),
                QueryParameter("follow", "Keep the connection open and stream new log lines as server-sent events", required: false, new JsonObject
                {
                    ["type"] = "boolean",
                    ["default"] = false
                }),
                QueryParameter("since", "Only return lines logged after this time. Use a duration such as 30s, 5m, 1h or 1d, or a date-time.", required: false, new JsonObject
                {
                    ["type"] = "string"
                })),
            Responses = Responses(
                ("200", new JsonObject
                {
                    ["description"] = "Log lines. Select the format with the Accept header: text/plain (default), application/json, or text/event-stream (also used when follow=true; combine with application/json for JSON events).",
                    ["content"] = new JsonObject
                    {
                        ["text/plain"] = new JsonObject { ["schema"] = new JsonObject { ["type"] = "string" } },
                        [JsonContentType] = new JsonObject
                        {
                            ["schema"] = new JsonObject
                            {
                                ["type"] = "array",
                                ["items"] = LogEntrySchema()
                            }
                        },
                        ["text/event-stream"] = new JsonObject { ["schema"] = new JsonObject { ["type"] = "string" } }
                    }
                }),
                ("404", Response("Dev Proxy isn't running in detached mode, or the log file wasn't found", "text/plain", new JsonObject { ["type"] = "string" })))
        },
        _ => null
    };

    private static JsonObject ProxyInfoSchema() => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["recording"] = new JsonObject
            {
                ["type"] = "boolean",
                ["description"] = "Whether Dev Proxy is recording requests"
            },
            ["configFile"] = new JsonObject
            {
                ["type"] = new JsonArray("string", "null"),
                ["description"] = "Path of the configuration file Dev Proxy loaded. Read-only."
            }
        }
    };

    private static JsonObject JwtOptionsSchema() => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["name"] = StringProperty("Name of the user to create the token for. Default: Dev Proxy"),
            ["audiences"] = StringArrayProperty("Audiences to create the token for. Default: [\"https://myserver.com\"]"),
            ["issuer"] = StringProperty("Issuer of the token. Default: dev-proxy"),
            ["roles"] = StringArrayProperty("Role claims to add to the token"),
            ["scopes"] = StringArrayProperty("Scope claims to add to the token"),
            ["claims"] = new JsonObject
            {
                ["type"] = "object",
                ["additionalProperties"] = new JsonObject { ["type"] = "string" },
                ["description"] = "Additional claims to add to the token"
            },
            ["validFor"] = new JsonObject
            {
                ["type"] = "number",
                ["description"] = "Token lifetime in minutes. Default: 60"
            },
            ["signingKey"] = new JsonObject
            {
                ["type"] = "string",
                ["minLength"] = 32,
                ["description"] = "Key to sign the token with. Default: random key"
            }
        },
        ["examples"] = new JsonArray(new JsonObject
        {
            ["name"] = "Dev Proxy",
            ["audiences"] = new JsonArray("https://myserver.com"),
            ["scopes"] = new JsonArray("read", "write"),
            ["validFor"] = 60
        })
    };

    // The logs endpoint serializes entries with default (PascalCase) naming.
    private static JsonObject LogEntrySchema() => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["Time"] = NullableStringProperty("Time the line was logged (HH:mm:ss.fff)"),
            ["Level"] = NullableStringProperty("Log level"),
            ["Category"] = NullableStringProperty("Log category"),
            ["Message"] = NullableStringProperty("Log message"),
            ["Raw"] = NullableStringProperty("Unparsed log line")
        }
    };

    private static JsonObject ValidationProblemSchema() => new()
    {
        ["type"] = "object",
        ["description"] = "RFC 9457 problem details",
        ["properties"] = new JsonObject
        {
            ["type"] = new JsonObject { ["type"] = "string" },
            ["title"] = new JsonObject { ["type"] = "string" },
            ["status"] = new JsonObject { ["type"] = "integer" },
            ["detail"] = new JsonObject { ["type"] = "string" },
            ["errors"] = new JsonObject
            {
                ["type"] = "object",
                ["additionalProperties"] = new JsonObject
                {
                    ["type"] = "array",
                    ["items"] = new JsonObject { ["type"] = "string" }
                }
            }
        }
    };

    private static JsonObject StringProperty(string description) => new()
    {
        ["type"] = "string",
        ["description"] = description
    };

    private static JsonObject NullableStringProperty(string description) => new()
    {
        ["type"] = new JsonArray("string", "null"),
        ["description"] = description
    };

    private static JsonObject StringArrayProperty(string description) => new()
    {
        ["type"] = "array",
        ["items"] = new JsonObject { ["type"] = "string" },
        ["description"] = description
    };

    private static JsonObject QueryParameter(string name, string description, bool required, JsonObject schema) => new()
    {
        ["name"] = name,
        ["description"] = description,
        ["required"] = required,
        ["schema"] = schema
    };

    private static JsonObject RequestBody(JsonObject schema) => new()
    {
        ["required"] = true,
        ["content"] = new JsonObject
        {
            [JsonContentType] = new JsonObject { ["schema"] = schema }
        }
    };

    private static JsonObject Response(string description, string? contentType = null, JsonObject? schema = null)
    {
        var response = new JsonObject { ["description"] = description };
        if (contentType is not null)
        {
            response["content"] = new JsonObject
            {
                [contentType] = schema is null ? new JsonObject() : new JsonObject { ["schema"] = schema }
            };
        }
        return response;
    }

    private static JsonObject Responses(params (string StatusCode, JsonObject Response)[] responses)
    {
        var result = new JsonObject();
        foreach (var (statusCode, response) in responses)
        {
            result[statusCode] = response;
        }
        return result;
    }
}
