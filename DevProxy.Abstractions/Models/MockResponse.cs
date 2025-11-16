// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Abstractions.Utils;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace DevProxy.Abstractions.Models;

public class MockResponse : ICloneable
{
    public MockResponseRequest? Request { get; set; }
    public MockResponseResponse? Response { get; set; }

    public object Clone()
    {
        var json = JsonSerializer.Serialize(this);
        return JsonSerializer.Deserialize<MockResponse>(json) ?? new MockResponse();
    }

    public static MockResponse FromHttpResponse(string httpResponse, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        
        if (logger.IsEnabled(LogLevel.Trace))
        {
            logger.LogTrace("{Method} called", nameof(FromHttpResponse));
        }

        if (string.IsNullOrWhiteSpace(httpResponse))
        {
            throw new ArgumentException("HTTP response cannot be null or empty.", nameof(httpResponse));
        }
        if (!httpResponse.StartsWith("HTTP/", StringComparison.Ordinal))
        {
            throw new ArgumentException("Invalid HTTP response format. HTTP response must begin with 'HTTP/'", nameof(httpResponse));
        }

        var lines = httpResponse.Split(["\r\n", "\n"], StringSplitOptions.TrimEntries);
        var statusCode = 200;
        List<MockResponseHeader>? responseHeaders = null;
        dynamic? body = null;

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (logger.IsEnabled(LogLevel.Trace))
            {
                logger.LogTrace("Processing line {LineNumber}: {LineContent}", i + 1, line);
            }

            if (i == 0)
            {
                // First line is the status line
                var parts = line.Split(' ', 3);
                if (parts.Length < 2)
                {
                    throw new ArgumentException("Invalid HTTP response format. First line must contain at least HTTP version and status code.", nameof(httpResponse));
                }

                statusCode = int.TryParse(parts[1], out var _statusCode) ? _statusCode : 200;
            }
            else if (string.IsNullOrEmpty(line))
            {
                // empty line indicates the end of headers and the start of the body
                var bodyContents = string.Join("\n", lines.Skip(i + 1));
                if (string.IsNullOrWhiteSpace(bodyContents))
                {
                    continue;
                }

                var contentType = responseHeaders?.FirstOrDefault(h => h.Name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))?.Value;
                if (contentType is not null && contentType.Contains("application/json", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        body = JsonSerializer.Deserialize<dynamic>(bodyContents, ProxyUtils.JsonSerializerOptions);
                    }
                    catch (JsonException ex)
                    {
                        logger.LogError(ex, "Failed to deserialize JSON body from HTTP response");
                        body = bodyContents;
                    }
                }
                else
                {
                    body = bodyContents;
                }

                break;
            }
            else
            {
                // Headers
                var headerParts = line.Split(':', 2);
                if (headerParts.Length < 2)
                {
                    logger.LogError($"Invalid HTTP response header format");
                    continue;
                }

                responseHeaders ??= [];
                responseHeaders.Add(new(headerParts[0].Trim(), headerParts[1].Trim()));
            }
        }

        var mockResponse = new MockResponse
        {
            Request = new()
            {
                Url = "*"
            },
            Response = new()
            {
                StatusCode = statusCode,
                Headers = responseHeaders,
                Body = body
            }
        };

        if (logger.IsEnabled(LogLevel.Trace))
        {
            logger.LogTrace("Left {Method}", nameof(FromHttpResponse));
        }

        return mockResponse;
    }
}

public class MockResponseRequest
{
    public string? BodyFragment { get; set; }
    public string Method { get; set; } = "GET";
    public int? Nth { get; set; }
    public string Url { get; set; } = string.Empty;
}

public class MockResponseResponse
{
    public dynamic? Body { get; set; }
    public IEnumerable<MockResponseHeader>? Headers { get; set; }
    public int? StatusCode { get; set; } = 200;
}

public class MockResponseHeader
{
    public string Name { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;

    public MockResponseHeader()
    {
    }

    public MockResponseHeader(string name, string value)
    {
        Name = name;
        Value = value;
    }
}