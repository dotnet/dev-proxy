// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

namespace DevProxy.Plugins.Utils;

internal sealed class MethodAndUrlComparer : IEqualityComparer<(string method, string url)>
{
    public bool Equals((string method, string url) x, (string method, string url) y) =>
        x.method == y.method && x.url == y.url;

    public int GetHashCode((string method, string url) obj)
    {
        var methodHashCode = obj.method.GetHashCode(StringComparison.OrdinalIgnoreCase);
        var urlHashCode = obj.url.GetHashCode(StringComparison.OrdinalIgnoreCase);

        return methodHashCode ^ urlHashCode;
    }
}