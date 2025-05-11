// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Abstractions.Plugins;
using DevProxy.Abstractions.Proxy;
using DevProxy.Abstractions.Utils;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace DevProxy.Plugins.Mocking;

internal sealed class MockRequestLoader(
    ILogger<MockRequestLoader> logger,
    MockRequestConfiguration configuration,
    IProxyConfiguration proxyConfiguration) :
    BaseLoader(logger, proxyConfiguration)
{
    private readonly MockRequestConfiguration _configuration = configuration;
    private readonly ILogger _logger = logger;

    protected override string FilePath => _configuration.MockFile;

    protected override void LoadData(string fileContents)
    {
        try
        {
            var requestConfig = JsonSerializer.Deserialize<MockRequestConfiguration>(fileContents, ProxyUtils.JsonSerializerOptions);
            var configRequest = requestConfig?.Request;
            if (configRequest is not null)
            {
                _configuration.Request = configRequest;
                _logger.LogInformation("Mock request to url {Url} loaded from {MockFile}", _configuration.Request.Url, _configuration.MockFile);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "An error has occurred while reading {ConfigurationFile}:", _configuration.MockFile);
        }
    }
}
