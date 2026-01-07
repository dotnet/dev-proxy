// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Abstractions.Plugins;
using DevProxy.Abstractions.Proxy;
using DevProxy.Abstractions.Utils;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;

namespace DevProxy.Plugins.Mocking;

internal sealed class CrudApiDataLoader(
    HttpClient httpClient,
    ILogger<CrudApiDataLoader> logger,
    CrudApiConfiguration configuration,
    IProxyConfiguration proxyConfiguration,
    Action<JArray?> onDataLoaded) :
    BaseLoader(httpClient, logger, proxyConfiguration)
{
    private readonly CrudApiConfiguration _configuration = configuration;
    private readonly Action<JArray?> _onDataLoaded = onDataLoaded;

    protected override string FilePath =>
        Path.GetFullPath(
            ProxyUtils.ReplacePathTokens(_configuration.DataFile),
            Path.GetDirectoryName(_configuration.ApiFile) ?? string.Empty
        );

    protected override void LoadData(string fileContents)
    {
        try
        {
            var data = JArray.Parse(fileContents);
            _onDataLoaded(data);
            Logger.LogInformation(
                "Data for CRUD API loaded from {DataFile} for API {ApiFile}",
                _configuration.DataFile,
                _configuration.ApiFile);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "An error has occurred while reading {DataFile}", _configuration.DataFile);
            _onDataLoaded(null);
        }
    }
}
