// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Abstractions.Plugins;
using DevProxy.Abstractions.Proxy;
using DevProxy.Abstractions.Utils;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace DevProxy.Plugins.Mocking;

/// <summary>
/// Loads (and hot-reloads) WebSocket mocks from the configured mocks file, mirroring
/// <see cref="MockResponsesLoader"/>.
/// </summary>
internal sealed class WebSocketMockResponsesLoader(
    HttpClient httpClient,
    ILogger<WebSocketMockResponsesLoader> logger,
    WebSocketMockResponseConfiguration configuration,
    IProxyConfiguration proxyConfiguration) :
    BaseLoader(httpClient, logger, proxyConfiguration)
{
    private readonly WebSocketMockResponseConfiguration _configuration = configuration;

    protected override string FilePath => _configuration.MocksFile;

    protected override void LoadData(string fileContents)
    {
        try
        {
            var config = JsonSerializer.Deserialize<WebSocketMockResponseConfiguration>(fileContents, ProxyUtils.JsonSerializerOptions);
            var mocks = config?.Mocks;
            if (mocks is not null)
            {
                _configuration.Mocks = mocks;
                if (Logger.IsEnabled(LogLevel.Information))
                {
                    Logger.LogInformation("WebSocket mocks for {MockCount} url patterns loaded from {MockFile}", mocks.Count(), _configuration.MocksFile);
                }
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "An error has occurred while reading {ConfigurationFile}:", _configuration.MocksFile);
        }
    }
}
