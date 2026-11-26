// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Net;

namespace DevProxy.Proxy;

/// <summary>
/// Sends requests directly instead of through a system proxy registration that
/// <see cref="SystemProxySession"/> kept from a previous run while the current run hasn't
/// bound its listener yet. Any other proxy, such as a separately configured upstream
/// proxy, is used as is.
/// </summary>
internal sealed class RetainedSystemProxyBypass(IWebProxy inner, SystemProxySession session) : IWebProxy
{
    public ICredentials? Credentials
    {
        get => inner.Credentials;
        set => inner.Credentials = value;
    }

    public Uri? GetProxy(Uri destination)
    {
        var proxy = inner.GetProxy(destination);
        return proxy is not null && session.IsRetainedUnboundEndpoint(proxy) ? null : proxy;
    }

    public bool IsBypassed(Uri host)
    {
        if (inner.IsBypassed(host))
        {
            return true;
        }

        var proxy = inner.GetProxy(host);
        return proxy is not null && session.IsRetainedUnboundEndpoint(proxy);
    }
}
