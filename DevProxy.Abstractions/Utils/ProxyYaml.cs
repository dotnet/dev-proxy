// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Text.Json;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace DevProxy.Abstractions.Utils;

/// <summary>
/// Utility class for YAML operations including conversion to JSON.
/// </summary>
public static class ProxyYaml
{
    /// <summary>
    /// Determines if a file path is a YAML file based on its extension.
    /// </summary>
    /// <param name="filePath">The file path to check.</param>
    /// <returns>True if the file is a YAML file, false otherwise.</returns>
    public static bool IsYamlFile(string? filePath)
    {
        if (string.IsNullOrEmpty(filePath))
        {
            return false;
        }

        var extension = Path.GetExtension(filePath);
        return extension.Equals(".yaml", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".yml", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Converts YAML content to JSON.
    /// </summary>
    /// <param name="yamlContent">The YAML content to convert.</param>
    /// <returns>The JSON representation of the YAML content.</returns>
    /// <exception cref="YamlException">Thrown when the YAML is invalid.</exception>
    public static string ConvertYamlToJson(string yamlContent)
    {
        ArgumentNullException.ThrowIfNull(yamlContent);

        // Parse the YAML using RepresentationModel which handles anchors/aliases natively
        var yaml = new YamlStream();
        using var reader = new StringReader(yamlContent);
        yaml.Load(reader);

        if (yaml.Documents.Count == 0 || yaml.Documents[0].RootNode is null)
        {
            return "{}";
        }

        var root = yaml.Documents[0].RootNode;
        var jsonObject = ConvertNode(root);

        return JsonSerializer.Serialize(jsonObject, ProxyUtils.JsonSerializerOptions);
    }

    /// <summary>
    /// Tries to convert YAML content to JSON.
    /// </summary>
    /// <param name="yamlContent">The YAML content to convert.</param>
    /// <param name="jsonContent">When successful, contains the JSON content; otherwise, null.</param>
    /// <param name="error">When unsuccessful, contains the error message; otherwise, null.</param>
    /// <returns>True if conversion was successful, false otherwise.</returns>
    public static bool TryConvertYamlToJson(string yamlContent, out string? jsonContent, out string? error)
    {
        try
        {
            jsonContent = ConvertYamlToJson(yamlContent);
            error = null;
            return true;
        }
        catch (YamlException ex)
        {
            jsonContent = null;
            error = ex.Message;
            return false;
        }
        catch (Exception ex)
        {
            jsonContent = null;
            error = ex.Message;
            return false;
        }
    }

    private static object? ConvertNode(YamlNode node)
    {
        return node switch
        {
            YamlMappingNode mappingNode => ConvertMappingNode(mappingNode),
            YamlSequenceNode sequenceNode => ConvertSequenceNode(sequenceNode),
            YamlScalarNode scalarNode => ConvertScalarNode(scalarNode),
            _ => null
        };
    }

    private static Dictionary<string, object?> ConvertMappingNode(YamlMappingNode mappingNode)
    {
        var result = new Dictionary<string, object?>();

        // First, process all merge keys to get merged values
        var mergedValues = new Dictionary<string, object?>();
        foreach (var entry in mappingNode.Children)
        {
            var key = ((YamlScalarNode)entry.Key).Value ?? string.Empty;

            if (key == "<<")
            {
                if (entry.Value is YamlMappingNode mergeMapping)
                {
                    var mergeDict = ConvertMappingNode(mergeMapping);
                    foreach (var kvp in mergeDict)
                    {
                        // Later merges override earlier merges
                        mergedValues[kvp.Key] = kvp.Value;
                    }
                }
                else if (entry.Value is YamlSequenceNode mergeSequence)
                {
                    // Handle merging multiple mappings
                    foreach (var item in mergeSequence.Children)
                    {
                        if (item is YamlMappingNode itemMapping)
                        {
                            var mergeDict = ConvertMappingNode(itemMapping);
                            foreach (var kvp in mergeDict)
                            {
                                mergedValues[kvp.Key] = kvp.Value;
                            }
                        }
                    }
                }
            }
        }

        // Add merged values to result first
        foreach (var kvp in mergedValues)
        {
            result[kvp.Key] = kvp.Value;
        }

        // Then, process explicit keys (they override merged values)
        foreach (var entry in mappingNode.Children)
        {
            var key = ((YamlScalarNode)entry.Key).Value ?? string.Empty;

            // Skip merge keys
            if (key == "<<")
            {
                continue;
            }

            // Explicit keys always override merged values
            result[key] = ConvertNode(entry.Value);
        }

        return result;
    }

    private static List<object?> ConvertSequenceNode(YamlSequenceNode sequenceNode)
    {
        var result = new List<object?>();
        foreach (var item in sequenceNode.Children)
        {
            result.Add(ConvertNode(item));
        }
        return result;
    }

    private static object? ConvertScalarNode(YamlScalarNode scalarNode)
    {
        var value = scalarNode.Value;
        
        if (value is null)
        {
            return null;
        }

        // Try to infer the type from the value
        // Check for boolean values
        if (value.Equals("true", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        if (value.Equals("false", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // Check for null values
        if (value.Equals("null", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("~", StringComparison.Ordinal) ||
            value.Length == 0)
        {
            return null;
        }

        // Check for integer values
        if (long.TryParse(value, out var longValue))
        {
            // Return int if it fits, otherwise long
            if (longValue >= int.MinValue && longValue <= int.MaxValue)
            {
                return (int)longValue;
            }
            return longValue;
        }

        // Check for floating point values
        if (double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var doubleValue))
        {
            return doubleValue;
        }

        // Return as string
        return value;
    }
}
