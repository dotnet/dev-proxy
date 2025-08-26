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
using System.Net.Http.Json;
using System.Text.Json;

namespace DevProxy.Plugins.Generation;

public sealed class MockGeneratorPlugin(
    ILogger<MockGeneratorPlugin> logger,
    ISet<UrlToWatch> urlsToWatch,
    IProxyStorage proxyStorage) : BaseReportingPlugin(logger, urlsToWatch, proxyStorage)
{
    public override string Name => nameof(MockGeneratorPlugin);

    public override Func<RecordingArgs, CancellationToken, Task>? HandleRecordingStopAsync => AfterRecordingStopAsync;
    public async Task AfterRecordingStopAsync(RecordingArgs e, CancellationToken cancellationToken)
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
              !ProxyUtils.MatchesUrlToWatch(UrlsToWatch, request.Request!.RequestUri!.AbsoluteUri))
            {
                continue;
            }

            var methodAndUrlString = request.Message;
            Logger.LogDebug("Processing request {MethodAndUrlString}...", methodAndUrlString);

            var (method, url) = GetMethodAndUrl(methodAndUrlString);
            var response = request.Response;

            if (response is null)
            {
                Logger.LogDebug("No response found for request {MethodAndUrlString}. Skipping", methodAndUrlString);
                continue;
            }

            var newHeaders = new List<MockResponseHeader>();
            newHeaders.AddRange(response.Headers.Select(h => new MockResponseHeader(h.Key, string.Join(';', h.Value))));
            var mock = new MockResponse
            {
                Request = new()
                {
                    Method = method,
                    Url = url,
                },
                Response = new()
                {
                    StatusCode = (int)response.StatusCode,
                    Headers = newHeaders,
                    Body = await GetResponseBodyAsync(request.Response!, cancellationToken)
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

        StoreReport(fileName);

        Logger.LogTrace("Left {Name}", nameof(AfterRecordingStopAsync));
    }

    /// <summary>
    /// Returns the body of the response. For binary responses,
    /// saves the binary response as a file on disk and returns @filename
    /// </summary>
    /// <param name="session">Request session</param>
    /// <returns>Response body or @filename for binary responses</returns>
    private async Task<dynamic?> GetResponseBodyAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        Logger.LogDebug("Getting response body...");

        if (response.Content is null)
        {
            Logger.LogDebug("Response has no content-type set or has no body. Skipping");
            return null;
        }

        if (response.Content.Headers.ContentType?.MediaType?.Contains("application/json", StringComparison.OrdinalIgnoreCase) ?? false)
        {
            Logger.LogDebug("Response is JSON");

            try
            {
                Logger.LogDebug("Reading response body as string...");
                return await response.Content.ReadFromJsonAsync<dynamic>(ProxyUtils.JsonSerializerOptions, cancellationToken);
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
            var body = await response.Content.ReadAsByteArrayAsync(cancellationToken);
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
