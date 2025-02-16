// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Microsoft.Extensions.Logging;
using DevProxy.Abstractions;
using System.Text.Json;

namespace DevProxy.Plugins.Behavior;

internal class RateLimitingCustomResponseLoader(ILogger logger, RateLimitConfiguration configuration, bool validateSchemas) : BaseLoader(logger, validateSchemas)
{
    private readonly ILogger _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly RateLimitConfiguration _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
    protected override string FilePath => Path.Combine(Directory.GetCurrentDirectory(), _configuration.CustomResponseFile);

    protected override void LoadData(string fileContents)
    {
        try
        {
            var response = JsonSerializer.Deserialize<MockResponseResponse>(fileContents, ProxyUtils.JsonSerializerOptions);
            if (response is not null)
            {
                _configuration.CustomResponse = response;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "An error has occurred while reading {configurationFile}:", _configuration.CustomResponseFile);
        }
    }
}
