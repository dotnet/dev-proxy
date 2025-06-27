// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Abstractions.Models;
using DevProxy.Abstractions.Plugins;
using DevProxy.Abstractions.Proxy;
using DevProxy.Abstractions.Utils;
using DevProxy.Plugins.Mocking;
using DevProxy.Plugins.Utils;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using Titanium.Web.Proxy.EventArguments;

namespace DevProxy.Plugins.Generation;

public sealed class MockGeneratorPlugin(
    ILogger<MockGeneratorPlugin> logger,
    ISet<UrlToWatch> urlsToWatch) : BaseReportingPlugin(logger, urlsToWatch)
{
    public override string Name => nameof(MockGeneratorPlugin);

    public override async Task AfterRecordingStopAsync(RecordingArgs e, CancellationToken cancellationToken)
    {
        Logger.LogTrace("{Method} called", nameof(AfterRecordingStopAsync));

        ArgumentNullException.ThrowIfNull(e);

        if (!e.RequestLogs.Any())
        {
            Logger.LogDebug("No requests to process");
            return;
        }

        Logger.LogInformation("Creating mocks from recorded requests...");

        var methodAndUrlComparer = new MethodAndUrlComparer();
        var mocks = new List<MockResponse>();

        foreach (var request in e.RequestLogs)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (request.MessageType != MessageType.InterceptedResponse ||
              request.Context is null ||
              request.Context.Session is null ||
              !ProxyUtils.MatchesUrlToWatch(UrlsToWatch, request.Context.Session.HttpClient.Request.RequestUri.AbsoluteUri))
            {
                continue;
            }

            var methodAndUrlString = request.Message;
            Logger.LogDebug("Processing request {MethodAndUrlString}...", methodAndUrlString);

            var (method, url) = GetMethodAndUrl(methodAndUrlString);
            var response = request.Context.Session.HttpClient.Response;

            var newHeaders = new List<MockResponseHeader>();
            newHeaders.AddRange(response.Headers.Select(h => new MockResponseHeader(h.Name, h.Value)));
            var mock = new MockResponse
            {
                Request = new()
                {
                    Method = method,
                    Url = url,
                },
                Response = new()
                {
                    StatusCode = response.StatusCode,
                    Headers = newHeaders,
                    Body = await GetResponseBodyAsync(request.Context.Session, cancellationToken)
                }
            };
            // skip mock if it's 200 but has no body
            if (mock.Response.StatusCode == 200 && mock.Response.Body is null)
            {
                Logger.LogDebug("Skipping mock with 200 response code and no body");
                continue;
            }

            mocks.Add(mock);
            Logger.LogDebug("Added mock for {Method} {Url}", mock.Request.Method, mock.Request.Url);
        }

        Logger.LogDebug("Sorting mocks...");
        // sort mocks descending by url length so that most specific mocks are first
        mocks.Sort((a, b) => string.CompareOrdinal(b.Request!.Url, a.Request!.Url));

        var mocksFile = new MockResponseConfiguration { Mocks = mocks };

        Logger.LogDebug("Serializing mocks...");
        var mocksFileJson = JsonSerializer.Serialize(mocksFile, ProxyUtils.JsonSerializerOptions);
        var fileName = $"mocks-{DateTime.Now:yyyyMMddHHmmss}.json";

        Logger.LogDebug("Writing mocks to {FileName}...", fileName);
        await File.WriteAllTextAsync(fileName, mocksFileJson, cancellationToken);

        Logger.LogInformation("Created mock file {FileName} with {MocksCount} mocks", fileName, mocks.Count);

        StoreReport(fileName, e);

        Logger.LogTrace("Left {Name}", nameof(AfterRecordingStopAsync));
    }

    /// <summary>
    /// Returns the body of the response. For binary responses,
    /// saves the binary response as a file on disk and returns @filename
    /// </summary>
    /// <param name="session">Request session</param>
    /// <returns>Response body or @filename for binary responses</returns>
    private async Task<dynamic?> GetResponseBodyAsync(SessionEventArgs session, CancellationToken cancellationToken)
    {
        Logger.LogDebug("Getting response body...");

        var response = session.HttpClient.Response;
        if (response.ContentType is null || !response.HasBody)
        {
            Logger.LogDebug("Response has no content-type set or has no body. Skipping");
            return null;
        }

        if (response.ContentType.Contains("application/json", StringComparison.OrdinalIgnoreCase))
        {
            Logger.LogDebug("Response is JSON");

            try
            {
                Logger.LogDebug("Reading response body as string...");
                var body = response.IsBodyRead ? response.BodyString : await session.GetResponseBodyAsString(cancellationToken);
                Logger.LogDebug("Body: {Body}", body);
                Logger.LogDebug("Deserializing response body...");
                return JsonSerializer.Deserialize<dynamic>(body, ProxyUtils.JsonSerializerOptions);
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Error reading response body");
                return null;
            }
        }

        Logger.LogDebug("Response is binary");
        // assume body is binary
        try
        {
            var filename = $"response-{Guid.NewGuid()}.bin";
            Logger.LogDebug("Reading response body as bytes...");
            var body = await session.GetResponseBody(cancellationToken);
            Logger.LogDebug("Writing response body to {Filename}...", filename);
            await File.WriteAllBytesAsync(filename, body, cancellationToken);
            return $"@{filename}";
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error reading response body");
            return null;
        }
    }

    private static (string method, string url) GetMethodAndUrl(string message)
    {
        var info = message.Split(" ");
        if (info.Length > 2)
        {
            info = [info[0], string.Join(" ", info.Skip(1))];
        }
        return (info[0], info[1]);
    }
}
