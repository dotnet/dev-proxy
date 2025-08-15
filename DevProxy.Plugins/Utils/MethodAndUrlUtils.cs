// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

namespace DevProxy.Plugins.Utils;

internal readonly record struct MethodAndUrl(string Method, string Url);

static class MethodAndUrlUtils
{
    internal static MethodAndUrl GetMethodAndUrl(string methodAndUrlString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(methodAndUrlString);

        var info = methodAndUrlString.Split(" ");
        if (info.Length > 2)
        {
            info = [info[0], string.Join(" ", info.Skip(1))];
        }
        return new(Method: info[0], Url: info[1]);
    }
}