// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

namespace DevProxy.Proxy;

sealed class ProcessEnvironment : IDisposable
{
    private readonly Dictionary<string, string?> _originalValues = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    public void Apply(IConfiguration configuration, IReadOnlyDictionary<string, string>? commandLineEnv)
    {
        var configEnv = configuration.GetSection("env").Get<Dictionary<string, string>>();
        foreach (var variables in new IEnumerable<KeyValuePair<string, string>>?[] { configEnv, commandLineEnv })
        {
            if (variables is null)
            {
                continue;
            }

            foreach (var (name, value) in variables)
            {
                var originalValue = Environment.GetEnvironmentVariable(name);
                Environment.SetEnvironmentVariable(name, value);
                _originalValues.TryAdd(name, originalValue);
            }
        }
    }

    public void Dispose()
    {
        foreach (var (name, value) in _originalValues)
        {
            Environment.SetEnvironmentVariable(name, value);
        }
        _originalValues.Clear();
    }
}
