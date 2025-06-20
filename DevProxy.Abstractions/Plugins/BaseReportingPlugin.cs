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
    ISet<UrlToWatch> urlsToWatch) : BasePlugin(logger, urlsToWatch)
{
    protected virtual void StoreReport(object report, ProxyEventArgsBase e)
    {
        ArgumentNullException.ThrowIfNull(e);

        if (report is null)
        {
            return;
        }

        ((Dictionary<string, object>)e.GlobalData[ProxyUtils.ReportsKey])[Name] = report;
    }
}

public abstract class BaseReportingPlugin<TConfiguration>(
    ILogger logger,
    ISet<UrlToWatch> urlsToWatch,
    IProxyConfiguration proxyConfiguration,
    IConfigurationSection configurationSection) :
    BasePlugin<TConfiguration>(
        logger,
        urlsToWatch,
        proxyConfiguration,
        configurationSection) where TConfiguration : new()
{
    protected virtual void StoreReport(object report, ProxyEventArgsBase e)
    {
        ArgumentNullException.ThrowIfNull(e);

        if (report is null)
        {
            return;
        }

        ((Dictionary<string, object>)e.GlobalData[ProxyUtils.ReportsKey])[Name] = report;
    }
}
