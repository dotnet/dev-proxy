// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Abstractions.Plugins;
using DevProxy.Abstractions.Proxy;
using DevProxy.Abstractions.Proxy.Http;
using DevProxy.Abstractions.Utils;
using DevProxy.Plugins.Models;
using DevProxy.Plugins.Utils;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Globalization;
using System.Net.WebSockets;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Web;

namespace DevProxy.Plugins.Generation;

public sealed class HarGeneratorPluginConfiguration
{
    public bool IncludeSensitiveInformation { get; set; }
    public bool IncludeResponse { get; set; }
}

public sealed class HarGeneratorPlugin(
    HttpClient httpClient,
    ILogger<HarGeneratorPlugin> logger,
    ISet<UrlToWatch> urlsToWatch,
    IProxyConfiguration proxyConfiguration,
    IConfigurationSection pluginConfigurationSection) :
    BaseReportingPlugin<HarGeneratorPluginConfiguration>(
        httpClient,
        logger,
        urlsToWatch,
        proxyConfiguration,
        pluginConfigurationSection)
{
    private static readonly Regex surrogatePairRegex = new(@"\\u([dD][89aAbB][0-9a-fA-F]{2})\\u([dD][cCdDeEfF][0-9a-fA-F]{2})");

    public override string Name => nameof(HarGeneratorPlugin);

    public override async Task AfterRecordingStopAsync(RecordingArgs e, CancellationToken cancellationToken)
    {
        Logger.LogTrace("{Method} called", nameof(AfterRecordingStopAsync));

        ArgumentNullException.ThrowIfNull(e);

        if (!e.RequestLogs.Any())
        {
            Logger.LogDebug("No requests to process");
            return;
        }

        Logger.LogInformation("Creating HAR file from recorded requests...");

        var harFile = new HarFile
        {
            Log = new HarLog
            {
                Creator = new HarCreator
                {
                    Name = "DevProxy",
                    Version = ProxyUtils.ProductVersion
                },
                Entries = [.. e.RequestLogs.Where(r =>
                    r.MessageType == MessageType.InterceptedResponse &&
                    r is not null &&
                    r.Context is not null &&
                    r.Context.Session is not null &&
                    ProxyUtils.MatchesUrlToWatch(UrlsToWatch, r.Context.Session.Request.RequestUri.AbsoluteUri)).Select(CreateHarEntry)]
            }
        };

        Logger.LogDebug("Serializing HAR file...");
        var harFileJson = JsonSerializer.Serialize(harFile, ProxyUtils.JsonSerializerOptions);
        harFileJson = UnescapeSurrogatePairs(harFileJson);
        var fileName = $"devproxy-{DateTime.Now:yyyyMMddHHmmss}.har";

        Logger.LogDebug("Writing HAR file to {FileName}...", fileName);
        await File.WriteAllTextAsync(fileName, harFileJson, cancellationToken);

        Logger.LogInformation("Created HAR file {FileName}", fileName);

        StoreReport(fileName, e);

        Logger.LogTrace("Left {Name}", nameof(AfterRecordingStopAsync));
    }

    private string GetHeaderValue(string headerName, string originalValue)
    {
        if (!Configuration.IncludeSensitiveInformation &&
            Http.SensitiveHeaders.Contains(headerName, StringComparer.OrdinalIgnoreCase))
        {
            return "REDACTED";
        }
        return originalValue;
    }

    private HarEntry CreateHarEntry(RequestLog log)
    {
        Debug.Assert(log is not null);
        Debug.Assert(log.Context is not null);

        var request = log.Context.Session.Request;
        var response = log.Context.Session.Response!;

        var entry = new HarEntry
        {
            StartedDateTime = log.Timestamp.UtcDateTime.ToString("o"),
            Time = 0, // We don't have actual timing data in RequestLog
            Request = new HarRequest
            {
                Method = request.Method,
                Url = request.RequestUri?.ToString(),
                HttpVersion = $"HTTP/{request.HttpVersion}",
                Headers = [.. request.Headers.Select(h => new HarHeader { Name = h.Name, Value = GetHeaderValue(h.Name, string.Join(", ", h.Value)) })],
                QueryString = [.. HttpUtility.ParseQueryString(request.RequestUri?.Query ?? "")
                    .AllKeys
                    .Where(key => key is not null)
                    .Select(key => new HarQueryParam { Name = key, Value = HttpUtility.ParseQueryString(request.RequestUri?.Query ?? "")[key] ?? "" })],
                Cookies = [.. request.Headers
                    .Where(h => string.Equals(h.Name, "Cookie", StringComparison.OrdinalIgnoreCase))
                    .Select(h => h.Value)
                    .SelectMany(v => v.Split(';'))
                    .Select(c =>
                    {
                        var parts = c.Split('=', 2);
                        return new HarCookie { Name = parts[0].Trim(), Value = parts.Length > 1 ? parts[1].Trim() : "" };
                    })],
                HeadersSize = request.Headers?.ToString()?.Length ?? 0,
                BodySize = request.HasBody ? request.Body.Length : 0,
                PostData = request.HasBody ? new HarPostData
                {
                    MimeType = request.ContentType,
                    Text = HttpUtils.GetBodyString(request.ContentType, request.Body.ToArray())
                }
                    : null
            },
            Response = response is not null ? new HarResponse
            {
                Status = (int)response.StatusCode,
                StatusText = response.StatusDescription,
                HttpVersion = $"HTTP/{response.HttpVersion}",
                Headers = [.. response.Headers.Select(h => new HarHeader { Name = h.Name, Value = GetHeaderValue(h.Name, string.Join(", ", h.Value)) })],
                Cookies = [.. response.Headers
                    .Where(h => string.Equals(h.Name, "Set-Cookie", StringComparison.OrdinalIgnoreCase))
                    .Select(h => h.Value)
                    .Select(sc =>
                    {
                        var parts = sc.Split(';')[0].Split('=', 2);
                        return new HarCookie { Name = parts[0].Trim(), Value = parts.Length > 1 ? parts[1].Trim() : "" };
                    })],
                Content = new HarContent
                {
                    Size = response.HasBody ? response.Body.Length : 0,
                    MimeType = response.ContentType ?? "",
                    Text = Configuration.IncludeResponse && response.HasBody ? HttpUtils.GetBodyString(response.ContentType, response.Body.ToArray()) : null
                },
                HeadersSize = response.Headers?.ToString()?.Length ?? 0,
                BodySize = response.HasBody ? response.Body.Length : 0
            } : null
        };

        // Attach WebSocket messages (if any) following the Chrome/mitmproxy convention.
        var wsMessages = log.Context.Session.WebSocketMessages;
        if (request.IsWebSocketRequest && wsMessages.Count > 0)
        {
            entry.ResourceType = "websocket";
            entry.WebSocketMessages = [.. wsMessages.Select(m =>
            {
                var isText = m.Type == WebSocketMessageType.Text;
                return new HarWebSocketMessage
                {
                    Type = m.Direction == WebSocketMessageDirection.Send ? "send" : "receive",
                    Time = m.Timestamp.ToUnixTimeMilliseconds() / 1000.0,
                    Opcode = ToRfc6455Opcode(m.Type),
                    Data = isText ? m.Text : Convert.ToBase64String(m.Data.Span)
                };
            })];
        }

        return entry;
    }

    // Maps the framework WebSocketMessageType to the RFC 6455 opcode used by the
    // Chrome DevTools / mitmproxy _webSocketMessages convention (1=text, 2=binary,
    // 8=close). WebSocketMessageType values (0/1/2) are NOT the wire opcodes.
    private static int ToRfc6455Opcode(WebSocketMessageType type) => type switch
    {
        WebSocketMessageType.Text => 1,
        WebSocketMessageType.Binary => 2,
        WebSocketMessageType.Close => 8,
        _ => 1
    };

    private static string UnescapeSurrogatePairs(string json)
    {
        return surrogatePairRegex.Replace(json, match =>
        {
            var high = int.Parse(match.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            var low = int.Parse(match.Groups[2].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            var codePoint = 0x10000 + ((high - 0xD800) << 10) + (low - 0xDC00);
            return char.ConvertFromUtf32(codePoint);
        });
    }
}