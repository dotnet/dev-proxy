// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Abstractions.Proxy;
using DevProxy.Abstractions.Utils;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace DevProxy.Abstractions.Plugins;

public abstract class BaseReportingPlugin(
    ILogger logger,
    ISet<UrlToWatch> urlsToWatch,
    IProxyStorage proxyStorage) : BasePlugin(logger, urlsToWatch)
{
    protected IProxyStorage ProxyStorage => proxyStorage;
    protected virtual void StoreReport(object report)
    {

        if (report is null)
        {
            return;
        }

        ((Dictionary<string, object>)ProxyStorage.GlobalData[ProxyUtils.ReportsKey])[Name] = report;
    }
}

public abstract class BaseReportingPlugin<TConfiguration>(
    HttpClient httpClient,
    ILogger logger,
    ISet<UrlToWatch> urlsToWatch,
    IProxyConfiguration proxyConfiguration,
    IConfigurationSection configurationSection,
    IProxyStorage proxyStorage) :
    BasePlugin<TConfiguration>(
        httpClient,
        logger,
        urlsToWatch,
        proxyConfiguration,
        configurationSection) where TConfiguration : new()
{
    protected IProxyStorage ProxyStorage => proxyStorage;
    protected virtual void StoreReport(object report)
    {

        if (report is null)
        {
            return;
        }

        ((Dictionary<string, object>)ProxyStorage.GlobalData[ProxyUtils.ReportsKey])[Name] = report;
    }
}
