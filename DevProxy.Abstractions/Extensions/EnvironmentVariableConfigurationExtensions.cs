// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Abstractions.Utils;
using Microsoft.Extensions.Configuration.Json;

#pragma warning disable IDE0130
namespace Microsoft.Extensions.Configuration;
#pragma warning restore IDE0130

/// <summary>
/// A configuration provider that resolves environment variable placeholders
/// (<c>${NAME}</c>, <c>${env:NAME}</c>) in configuration values.
/// </summary>
public interface IEnvironmentVariablePlaceholderProvider
{
    /// <summary>
    /// Path of the configuration file.
    /// </summary>
    string? Path { get; }

    /// <summary>
    /// Names of environment variables referenced in the configuration file that couldn't be resolved during the last load.
    /// </summary>
    IReadOnlyCollection<string> UnresolvedEnvironmentVariables { get; }
}

/// <summary>
/// JSON configuration source that resolves environment variable placeholders.
/// </summary>
public sealed class EnvironmentVariableJsonConfigurationSource : JsonConfigurationSource
{
    /// <summary>
    /// Variables that take precedence over the <c>env</c> configuration section and process environment variables.
    /// </summary>
    public IReadOnlyDictionary<string, string>? EnvironmentVariables { get; set; }

    /// <inheritdoc/>
    public override IConfigurationProvider Build(IConfigurationBuilder builder)
    {
        EnsureDefaults(builder);
        return new EnvironmentVariableJsonConfigurationProvider(this);
    }
}

/// <summary>
/// JSON configuration provider that resolves environment variable placeholders.
/// </summary>
public sealed class EnvironmentVariableJsonConfigurationProvider(EnvironmentVariableJsonConfigurationSource source) :
    JsonConfigurationProvider(source), IEnvironmentVariablePlaceholderProvider
{
    /// <inheritdoc/>
    public string? Path => source.Path;

    /// <inheritdoc/>
    public IReadOnlyCollection<string> UnresolvedEnvironmentVariables { get; private set; } = [];

    /// <inheritdoc/>
    public override void Load(Stream stream)
    {
        base.Load(stream);
        UnresolvedEnvironmentVariables = ConfigurationPlaceholderResolver.Resolve(Data, source.EnvironmentVariables);
    }
}

/// <summary>
/// Resolves environment variable placeholders in flattened configuration data.
/// </summary>
public static class ConfigurationPlaceholderResolver
{
    private const string EnvSectionPrefix = "env:";

    private static StringComparer VariableNameComparer =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    /// <summary>
    /// Resolves placeholders in configuration values in place. Variables are looked up in
    /// <paramref name="environmentVariables"/> first, then in the <c>env</c> configuration
    /// section, and finally in process environment variables. Values in the <c>env</c>
    /// section itself are resolved against <paramref name="environmentVariables"/> and
    /// process environment variables only.
    /// </summary>
    /// <returns>Names of variables that couldn't be resolved.</returns>
    public static IReadOnlyCollection<string> Resolve(IDictionary<string, string?> data, IReadOnlyDictionary<string, string>? environmentVariables)
    {
        ArgumentNullException.ThrowIfNull(data);

        var unresolved = new List<string>();
        Dictionary<string, string>? overrides = null;
        if (environmentVariables is not null)
        {
            overrides = new(VariableNameComparer);
            // use the indexer so that names differing only in casing on Windows don't throw; last value wins
            foreach (var (name, value) in environmentVariables)
            {
                overrides[name] = value;
            }
        }

        string? baseLookup(string name)
        {
            return overrides is not null && overrides.TryGetValue(name, out var value)
                ? value
                : Environment.GetEnvironmentVariable(name);
        }

        var envSection = new Dictionary<string, string>(VariableNameComparer);
        foreach (var key in data.Keys.ToArray())
        {
            if (!IsEnvSectionVariable(key))
            {
                continue;
            }

            var value = data[key];
            if (value is not null)
            {
                value = EnvironmentVariablePlaceholders.Resolve(value, baseLookup, unresolved);
                data[key] = value;
                envSection[key[EnvSectionPrefix.Length..]] = value;
            }
        }

        string? lookup(string name)
        {
            if (overrides is not null && overrides.TryGetValue(name, out var value))
            {
                return value;
            }
            return envSection.TryGetValue(name, out value) ? value : Environment.GetEnvironmentVariable(name);
        }

        var rewriteRulePrefixes = GetRewriteRulePrefixes(data, lookup);
        foreach (var key in data.Keys.ToArray())
        {
            if (IsEnvSectionVariable(key) ||
                rewriteRulePrefixes.Any(p => key.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var value = data[key];
            if (value is not null)
            {
                data[key] = EnvironmentVariablePlaceholders.Resolve(value, lookup, unresolved);
            }
        }

        return unresolved;
    }

    // RewritePlugin rules use ${name} for regex named groups and resolve ${env:NAME}
    // per request, so inline rules are excluded from load-time resolution
    private static string[] GetRewriteRulePrefixes(IDictionary<string, string?> data, Func<string, string?> lookup)
    {
        // plugin names and sections may use placeholders too; resolve them here without
        // changing data, which is resolved afterwards
        string? resolve(string? value)
        {
            return value is null ? null : EnvironmentVariablePlaceholders.Resolve(value, lookup);
        }

        const string pluginsPrefix = "plugins:";
        const string nameSuffix = ":name";
        const string rewritePluginName = "RewritePlugin";

        return [.. data
            .Where(e =>
                e.Key.StartsWith(pluginsPrefix, StringComparison.OrdinalIgnoreCase) &&
                e.Key.EndsWith(nameSuffix, StringComparison.OrdinalIgnoreCase) &&
                e.Key.Count(c => c == ':') == 2 &&
                string.Equals(resolve(e.Value), rewritePluginName, StringComparison.Ordinal))
            .Select(e => data.TryGetValue($"{e.Key[..^nameSuffix.Length]}:configSection", out var section) ? resolve(section) : null)
            .Where(section => !string.IsNullOrEmpty(section))
            .Select(section => $"{section}:rewrites:")];
    }

    private static bool IsEnvSectionVariable(string key) =>
        key.StartsWith(EnvSectionPrefix, StringComparison.OrdinalIgnoreCase) &&
        key.IndexOf(ConfigurationPath.KeyDelimiter, EnvSectionPrefix.Length, StringComparison.Ordinal) < 0;
}
