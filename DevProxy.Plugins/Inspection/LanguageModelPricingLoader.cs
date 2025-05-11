// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Abstractions.LanguageModel;
using DevProxy.Abstractions.Plugins;
using DevProxy.Abstractions.Proxy;
using DevProxy.Abstractions.Utils;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace DevProxy.Plugins.Inspection;

internal sealed class LanguageModelPricesLoader(
    ILogger<LanguageModelPricesLoader> logger,
    LanguageModelPricesPluginConfiguration configuration,
    IProxyConfiguration proxyConfiguration) :
    BaseLoader(logger, proxyConfiguration)
{
    private readonly LanguageModelPricesPluginConfiguration _configuration = configuration;
    private readonly ILogger _logger = logger;

    protected override string FilePath => _configuration.PricesFile ?? throw new ArgumentNullException(nameof(_configuration.PricesFile), "Prices file path must be set in the configuration.");

    protected override void LoadData(string fileContents)
    {
        try
        {
            // we need to deserialize manually because standard deserialization
            // doesn't support nested dictionaries
            using var document = JsonDocument.Parse(fileContents, ProxyUtils.JsonDocumentOptions);

            if (document.RootElement.TryGetProperty("prices", out var pricesElement))
            {
                var pricesData = new PricesData();

                foreach (var modelProperty in pricesElement.EnumerateObject())
                {
                    var modelName = modelProperty.Name;
                    if (modelProperty.Value.TryGetProperty("input", out var inputElement) &&
                        modelProperty.Value.TryGetProperty("output", out var outputElement))
                    {
                        pricesData[modelName] = new()
                        {
                            Input = inputElement.GetDouble(),
                            Output = outputElement.GetDouble()
                        };
                    }
                }

                _configuration.Prices = pricesData;
                _logger.LogInformation("Language model prices data loaded from {PricesFile}", _configuration.PricesFile);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "An error has occurred while reading {PricesFile}:", _configuration.PricesFile);
        }
    }
}
