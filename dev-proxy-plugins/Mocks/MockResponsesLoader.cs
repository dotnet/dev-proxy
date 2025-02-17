// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Abstractions;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace DevProxy.Plugins.Mocks;

internal class MockResponsesLoader(ILogger logger, MockResponseConfiguration configuration, bool validateSchemas) : BaseLoader(logger, validateSchemas)
{
    private readonly ILogger _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly MockResponseConfiguration _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
    protected override string FilePath => Path.Combine(Directory.GetCurrentDirectory(), _configuration.MocksFile);

    protected override void LoadData(string fileContents)
    {
        try
        {
            var responsesConfig = JsonSerializer.Deserialize<MockResponseConfiguration>(fileContents, ProxyUtils.JsonSerializerOptions);
            IEnumerable<MockResponse>? configResponses = responsesConfig?.Mocks;
            if (configResponses is not null)
            {
                _configuration.Mocks = configResponses;
                _logger.LogInformation("Mock responses for {configResponseCount} url patterns loaded from {mockFile}", configResponses.Count(), _configuration.MocksFile);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "An error has occurred while reading {configurationFile}:", _configuration.MocksFile);
        }
    }
}
