// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Abstractions;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace DevProxy.Plugins.RandomErrors;

internal class GenericErrorResponsesLoader(ILogger logger, GenericRandomErrorConfiguration configuration, bool validateSchemas) : BaseLoader(logger, validateSchemas)
{
    private readonly ILogger _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly GenericRandomErrorConfiguration _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
    protected override string FilePath => Path.Combine(Directory.GetCurrentDirectory(), _configuration.ErrorsFile ?? "");

    protected override void LoadData(string fileContents)
    {
        try
        {
            var responsesConfig = JsonSerializer.Deserialize<GenericRandomErrorConfiguration>(fileContents, ProxyUtils.JsonSerializerOptions);
            IEnumerable<GenericErrorResponse>? configResponses = responsesConfig?.Errors;
            if (configResponses is not null)
            {
                _configuration.Errors = configResponses;
                _logger.LogInformation("{configResponseCount} error responses loaded from {errorFile}", configResponses.Count(), _configuration.ErrorsFile);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "An error has occurred while reading {configurationFile}:", _configuration.ErrorsFile);
        }
    }
}
