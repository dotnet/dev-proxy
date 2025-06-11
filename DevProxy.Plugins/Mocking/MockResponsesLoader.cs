// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Abstractions.Plugins;
using DevProxy.Abstractions.Proxy;
using DevProxy.Abstractions.Utils;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace DevProxy.Plugins.Mocking;

internal sealed class MockResponsesLoader(
    ILogger<MockResponsesLoader> logger,
    MockResponseConfiguration configuration,
    IProxyConfiguration proxyConfiguration) : BaseLoader(logger, proxyConfiguration)
{
    private readonly MockResponseConfiguration _configuration = configuration;
    private readonly ILogger _logger = logger;

    protected override string FilePath => _configuration.MocksFile;

    protected override void LoadData(string fileContents)
    {
        try
        {
            var responsesConfig = JsonSerializer.Deserialize<MockResponseConfiguration>(fileContents, ProxyUtils.JsonSerializerOptions);
            var configResponses = responsesConfig?.Mocks;
            if (configResponses is not null)
            {
                _configuration.Mocks = configResponses;
                _logger.LogInformation("Mock responses for {ConfigResponseCount} url patterns loaded from {MockFile}", configResponses.Count(), _configuration.MocksFile);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "An error has occurred while reading {ConfigurationFile}:", _configuration.MocksFile);
        }
    }
}
