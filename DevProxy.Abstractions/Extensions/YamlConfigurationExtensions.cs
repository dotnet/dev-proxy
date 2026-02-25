// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Abstractions.Utils;
using YamlDotNet.RepresentationModel;

#pragma warning disable IDE0130
namespace Microsoft.Extensions.Configuration;
#pragma warning restore IDE0130

/// <summary>
/// A YAML file configuration source.
/// </summary>
public sealed class YamlConfigurationSource : FileConfigurationSource
{
    /// <inheritdoc/>
    public override IConfigurationProvider Build(IConfigurationBuilder builder)
    {
        EnsureDefaults(builder);
        return new YamlConfigurationProvider(this);
    }
}

/// <summary>
/// A YAML file configuration provider that supports anchors and merge keys.
/// </summary>
public sealed class YamlConfigurationProvider : FileConfigurationProvider
{
    /// <summary>
    /// Initializes a new instance of the <see cref="YamlConfigurationProvider"/> class.
    /// </summary>
    /// <param name="source">The configuration source.</param>
    public YamlConfigurationProvider(YamlConfigurationSource source) : base(source)
    {
    }

    /// <inheritdoc/>
    public override void Load(Stream stream)
    {
        using var reader = new StreamReader(stream);
        var yamlContent = reader.ReadToEnd();

        // Parse the YAML using RepresentationModel which handles anchors/aliases natively
        var yaml = new YamlStream();
        using var stringReader = new StringReader(yamlContent);
        yaml.Load(stringReader);

        Data = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        if (yaml.Documents.Count == 0 || yaml.Documents[0].RootNode is null)
        {
            return;
        }

        if (yaml.Documents[0].RootNode is YamlMappingNode mappingNode)
        {
            FlattenYamlNode(mappingNode, string.Empty);
        }
    }

    private void FlattenYamlNode(YamlNode node, string prefix)
    {
        switch (node)
        {
            case YamlMappingNode mappingNode:
                FlattenMappingNode(mappingNode, prefix);
                break;
            case YamlSequenceNode sequenceNode:
                FlattenSequenceNode(sequenceNode, prefix);
                break;
            case YamlScalarNode scalarNode:
                Data[prefix] = NormalizeScalar(scalarNode);
                break;
        }
    }

    private void FlattenMappingNode(YamlMappingNode mappingNode, string prefix)
    {
        // First, collect all merge key values
        var mergedValues = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in mappingNode.Children)
        {
            var key = GetScalarValue(entry.Key);
            if (key is null)
            {
                continue;
            }

            // Handle YAML merge key (<<)
            if (key == "<<")
            {
                if (entry.Value is YamlMappingNode mergeMapping)
                {
                    CollectMergedValues(mergeMapping, string.Empty, mergedValues);
                }
                else if (entry.Value is YamlSequenceNode mergeSequence)
                {
                    foreach (var item in mergeSequence.Children)
                    {
                        if (item is YamlMappingNode itemMapping)
                        {
                            CollectMergedValues(itemMapping, string.Empty, mergedValues);
                        }
                    }
                }
            }
        }

        // Add merged values first (they can be overridden by explicit values)
        foreach (var kvp in mergedValues)
        {
            var fullKey = string.IsNullOrEmpty(prefix)
                ? kvp.Key
                : $"{prefix}{ConfigurationPath.KeyDelimiter}{kvp.Key}";
            Data[fullKey] = kvp.Value;
        }

        // Then process regular keys (they override merged values)
        foreach (var entry in mappingNode.Children)
        {
            var key = GetScalarValue(entry.Key);
            if (key is null)
            {
                continue;
            }

            // Skip merge key
            if (key == "<<")
            {
                continue;
            }

            var newPrefix = string.IsNullOrEmpty(prefix)
                ? key
                : $"{prefix}{ConfigurationPath.KeyDelimiter}{key}";

            FlattenYamlNode(entry.Value, newPrefix);
        }
    }

    private static string? GetScalarValue(YamlNode node)
    {
        return node is YamlScalarNode scalarNode ? scalarNode.Value : null;
    }

    private static string? NormalizeScalar(YamlScalarNode scalarNode)
    {
        var value = scalarNode.Value;
        if (value is null)
        {
            return null;
        }

        // Only normalize plain (unquoted) scalars
        if (scalarNode.Style != YamlDotNet.Core.ScalarStyle.Plain)
        {
            return value;
        }

        return value.ToLowerInvariant() switch
        {
            "y" or "yes" or "true" or "on" => "true",
            "n" or "no" or "false" or "off" => "false",
            "~" or "null" or "" => null,
            _ => value
        };
    }

    private void CollectMergedValues(YamlMappingNode mappingNode, string prefix, Dictionary<string, string?> values)
    {
        foreach (var entry in mappingNode.Children)
        {
            var key = GetScalarValue(entry.Key);
            if (key is null)
            {
                continue;
            }

            // Handle nested merge keys recursively
            if (key == "<<")
            {
                switch (entry.Value)
                {
                    case YamlMappingNode mergeMapping:
                        CollectMergedValues(mergeMapping, prefix, values);
                        break;
                    case YamlSequenceNode mergeSequence:
                        foreach (var child in mergeSequence.Children)
                        {
                            if (child is YamlMappingNode childMapping)
                            {
                                CollectMergedValues(childMapping, prefix, values);
                            }
                        }
                        break;
                }
                continue;
            }

            var newPrefix = string.IsNullOrEmpty(prefix)
                ? key
                : $"{prefix}{ConfigurationPath.KeyDelimiter}{key}";

            CollectMergedValuesFromNode(entry.Value, newPrefix, values);
        }
    }

    private void CollectMergedValuesFromNode(YamlNode node, string prefix, Dictionary<string, string?> values)
    {
        switch (node)
        {
            case YamlMappingNode mappingNode:
                CollectMergedValues(mappingNode, prefix, values);
                break;
            case YamlSequenceNode sequenceNode:
                for (int i = 0; i < sequenceNode.Children.Count; i++)
                {
                    var newPrefix = $"{prefix}{ConfigurationPath.KeyDelimiter}{i}";
                    CollectMergedValuesFromNode(sequenceNode.Children[i], newPrefix, values);
                }
                break;
            case YamlScalarNode scalarNode:
                // Later values override earlier values within merged content
                values[prefix] = scalarNode.Value;
                break;
        }
    }

    private void FlattenSequenceNode(YamlSequenceNode sequenceNode, string prefix)
    {
        for (int i = 0; i < sequenceNode.Children.Count; i++)
        {
            var newPrefix = $"{prefix}{ConfigurationPath.KeyDelimiter}{i}";
            FlattenYamlNode(sequenceNode.Children[i], newPrefix);
        }
    }
}

/// <summary>
/// Extension methods for adding YAML configuration.
/// </summary>
public static class YamlConfigurationExtensions
{
    /// <summary>
    /// Adds a YAML configuration source to the configuration builder.
    /// </summary>
    /// <param name="builder">The configuration builder.</param>
    /// <param name="path">The path to the YAML file.</param>
    /// <param name="optional">Whether the file is optional.</param>
    /// <param name="reloadOnChange">Whether to reload on change.</param>
    /// <returns>The configuration builder.</returns>
    public static IConfigurationBuilder AddYamlFile(
        this IConfigurationBuilder builder,
        string path,
        bool optional = false,
        bool reloadOnChange = false)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(path);

        return builder.Add<YamlConfigurationSource>(s =>
        {
            s.FileProvider = null;
            s.Path = path;
            s.Optional = optional;
            s.ReloadOnChange = reloadOnChange;
            s.ResolveFileProvider();
        });
    }

    /// <summary>
    /// Adds a configuration file (JSON or YAML based on extension).
    /// </summary>
    /// <param name="builder">The configuration builder.</param>
    /// <param name="path">The path to the configuration file.</param>
    /// <param name="optional">Whether the file is optional.</param>
    /// <param name="reloadOnChange">Whether to reload on change.</param>
    /// <returns>The configuration builder.</returns>
    public static IConfigurationBuilder AddConfigFile(
        this IConfigurationBuilder builder,
        string path,
        bool optional = false,
        bool reloadOnChange = false)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(path);

        if (ProxyYaml.IsYamlFile(path))
        {
            return builder.AddYamlFile(path, optional, reloadOnChange);
        }

        return builder.AddJsonFile(path, optional, reloadOnChange);
    }
}
