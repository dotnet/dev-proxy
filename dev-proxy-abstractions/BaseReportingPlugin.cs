// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Microsoft.DevProxy.Abstractions;

public abstract class BaseReportingPlugin(IPluginEvents pluginEvents, IProxyContext context, ILogger logger, ISet<UrlToWatch> urlsToWatch, IConfigurationSection? configSection = null) : BaseProxyPlugin(pluginEvents, context, logger, urlsToWatch, configSection)
{
    protected virtual void StoreReport(object report, ProxyEventArgsBase e)
    {
        if (report is null)
        {
            return;
        }

        ((Dictionary<string, object>)e.GlobalData[ProxyUtils.ReportsKey])[Name] = report;
    }
}
