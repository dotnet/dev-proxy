// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Abstractions.Plugins;
using DevProxy.Abstractions.Proxy;
using Microsoft.Extensions.Logging;

namespace DevProxy.Plugins.Mocking;

internal sealed class OpenApiMockResponseSpecLoader(
    HttpClient httpClient,
    ILogger<OpenApiMockResponseSpecLoader> logger,
    OpenApiMockResponsePluginConfiguration configuration,
    Action<string> onSpecLoaded,
    IProxyConfiguration proxyConfiguration) :
    BaseLoader(httpClient, logger, proxyConfiguration)
{
    private readonly OpenApiMockResponsePluginConfiguration _configuration = configuration;
    private readonly Action<string> _onSpecLoaded = onSpecLoaded;

    protected override string FilePath => _configuration.SpecFile!;

    protected override void LoadData(string fileContents) => _onSpecLoaded(fileContents);
}
