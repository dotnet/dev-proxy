// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Abstractions.Utils;
using DevProxy.Abstractions.Proxy;
using DevProxy.Abstractions.Plugins;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using DevProxy.Abstractions.Models;

namespace DevProxy.Plugins.Behavior;

internal sealed class RateLimitingCustomResponseLoader(
    HttpClient httpClient,
    ILogger<RateLimitingCustomResponseLoader> logger,
    RateLimitConfiguration configuration,
    IProxyConfiguration proxyConfiguration) :
    BaseLoader(httpClient, logger, proxyConfiguration)
{
    private readonly RateLimitConfiguration _configuration = configuration;

    protected override string FilePath => _configuration.CustomResponseFile;

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
            Logger.LogError(ex, "An error has occurred while reading {ConfigurationFile}:", _configuration.CustomResponseFile);
        }
    }
}
