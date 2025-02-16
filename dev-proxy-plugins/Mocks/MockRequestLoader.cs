// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Abstractions;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace DevProxy.Plugins.Mocks;

internal class MockRequestLoader(ILogger logger, MockRequestConfiguration configuration, bool validateSchemas) : BaseLoader(logger, validateSchemas)
{
    private readonly ILogger _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly MockRequestConfiguration _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
    protected override string FilePath => Path.Combine(Directory.GetCurrentDirectory(), _configuration.MockFile);

    protected override void LoadData(string fileContents)
    {
        try
        {
            var requestConfig = JsonSerializer.Deserialize<MockRequestConfiguration>(fileContents, ProxyUtils.JsonSerializerOptions);
            var configRequest = requestConfig?.Request;
            if (configRequest is not null)
            {
                _configuration.Request = configRequest;
                _logger.LogInformation("Mock request to url {url} loaded from {mockFile}", _configuration.Request.Url, _configuration.MockFile);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "An error has occurred while reading {configurationFile}:", _configuration.MockFile);
        }
    }
}
