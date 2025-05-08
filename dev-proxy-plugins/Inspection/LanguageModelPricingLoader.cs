// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Abstractions;
using DevProxy.Abstractions.LanguageModel;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace DevProxy.Plugins.Inspection;

internal class LanguageModelPricesLoader(ILogger logger, LanguageModelPricesPluginConfiguration configuration, bool validateSchemas) : BaseLoader(logger, validateSchemas)
{
    private readonly ILogger _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly LanguageModelPricesPluginConfiguration _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
    protected override string FilePath => Path.Combine(Directory.GetCurrentDirectory(), _configuration.PricesFile ?? "");

    protected override void LoadData(string fileContents)
    {
        try
        {
            // we need to deserialize manually because standard deserialization
            // doesn't support nested dictionaries
            using JsonDocument document = JsonDocument.Parse(fileContents);

            if (document.RootElement.TryGetProperty("prices", out JsonElement pricesElement))
            {
                var pricesData = new PricesData();

                foreach (JsonProperty modelProperty in pricesElement.EnumerateObject())
                {
                    var modelName = modelProperty.Name;
                    if (modelProperty.Value.TryGetProperty("input", out JsonElement inputElement) &&
                        modelProperty.Value.TryGetProperty("output", out JsonElement outputElement))
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
