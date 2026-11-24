// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

namespace DevProxy.Proxy;

sealed class HttpProxyEnvironment
{
    private static readonly string[] VariableNames = OperatingSystem.IsWindows()
        ? ["HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY", "NO_PROXY"]
        : ["HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY", "NO_PROXY", "http_proxy", "https_proxy", "all_proxy", "no_proxy"];

    private Dictionary<string, string?>? _initialValues;

    public string[] GetChangedVariables()
    {
        if (_initialValues is null)
        {
            _initialValues = VariableNames.ToDictionary(name => name, Environment.GetEnvironmentVariable);
            return [];
        }

        return VariableNames
            .Where(name => !string.Equals(_initialValues[name], Environment.GetEnvironmentVariable(name), StringComparison.Ordinal))
            .ToArray();
    }
}
