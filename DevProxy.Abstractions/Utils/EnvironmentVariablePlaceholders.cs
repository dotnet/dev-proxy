// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Microsoft.Extensions.Logging;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using YamlDotNet.RepresentationModel;

namespace DevProxy.Abstractions.Utils;

/// <summary>
/// Resolves environment variable placeholders in user-authored files.
/// Supports <c>${NAME}</c> and <c>${env:NAME}</c>. Use <c>$${NAME}</c> to
/// output a literal <c>${NAME}</c>. Placeholders that reference variables
/// that aren't set are left as-is.
/// </summary>
public static partial class EnvironmentVariablePlaceholders
{
    private static readonly JsonSerializerOptions _writeOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>
    /// Returns true if the value contains something that looks like a placeholder or an escaped placeholder.
    /// </summary>
    public static bool ContainsPlaceholder(string? value) =>
        !string.IsNullOrEmpty(value) && PlaceholderRegex().IsMatch(value);

    /// <summary>
    /// Resolves placeholders in a string value using process environment variables.
    /// </summary>
    public static string Resolve(string value, ICollection<string>? unresolvedVariables = null) =>
        Resolve(value, Environment.GetEnvironmentVariable, unresolvedVariables);

    /// <summary>
    /// Resolves placeholders in a string value using the specified lookup.
    /// </summary>
    /// <param name="value">Value to resolve.</param>
    /// <param name="lookup">Function that returns the value of a variable or null if it isn't set.</param>
    /// <param name="unresolvedVariables">Collects names of variables that couldn't be resolved.</param>
    public static string Resolve(string value, Func<string, string?> lookup, ICollection<string>? unresolvedVariables = null) =>
        Resolve(value, lookup, unresolvedVariables, null);

    /// <summary>
    /// Resolves placeholders in a string value using the specified lookup.
    /// </summary>
    /// <param name="value">Value to resolve.</param>
    /// <param name="lookup">Function that returns the value of a variable or null if it isn't set.</param>
    /// <param name="unresolvedVariables">Collects names of variables that couldn't be resolved.</param>
    /// <param name="transformText">Optional transformation applied only to the text outside placeholders.</param>
    public static string Resolve(string value, Func<string, string?> lookup, ICollection<string>? unresolvedVariables, Func<string, string>? transformText)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(lookup);

        if (transformText is not null)
        {
            return ResolveWithTextTransform(value, lookup, unresolvedVariables, transformText);
        }

        if (!value.Contains("${", StringComparison.Ordinal))
        {
            return value;
        }

        return PlaceholderRegex().Replace(value, match => ResolveMatch(match, lookup, unresolvedVariables));
    }

    /// <summary>
    /// Applies a transformation to the text outside placeholders, leaving placeholders
    /// (including escaped ones) unchanged.
    /// </summary>
    public static string TransformOutsidePlaceholders(string value, Func<string, string> transformText)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(transformText);

        var result = new StringBuilder();
        var position = 0;
        foreach (Match match in PlaceholderRegex().Matches(value))
        {
            _ = result
                .Append(transformText(value[position..match.Index]))
                .Append(match.Value);
            position = match.Index + match.Length;
        }
        _ = result.Append(transformText(value[position..]));
        return result.ToString();
    }

    private static string ResolveWithTextTransform(string value, Func<string, string?> lookup, ICollection<string>? unresolvedVariables, Func<string, string> transformText)
    {
        var result = new StringBuilder();
        var position = 0;
        foreach (Match match in PlaceholderRegex().Matches(value))
        {
            _ = result
                .Append(transformText(value[position..match.Index]))
                .Append(ResolveMatch(match, lookup, unresolvedVariables));
            position = match.Index + match.Length;
        }
        _ = result.Append(transformText(value[position..]));
        return result.ToString();
    }

    private static string ResolveMatch(Match match, Func<string, string?> lookup, ICollection<string>? unresolvedVariables)
    {
        var name = match.Groups["name"].Value;
        if (match.Groups["escape"].Success)
        {
            // $${NAME} -> literal ${NAME}
            return match.Value[1..];
        }

        var variableValue = lookup(name);
        if (variableValue is null)
        {
            if (unresolvedVariables is not null && !unresolvedVariables.Contains(name))
            {
                unresolvedVariables.Add(name);
            }
            return match.Value;
        }

        return variableValue;
    }

    /// <summary>
    /// Resolves placeholders in all string values of a JSON document. Property names aren't changed.
    /// Returns the original string if it doesn't contain any placeholders.
    /// </summary>
    public static string ResolveInJson(string json, Func<string, string?> lookup, ICollection<string>? unresolvedVariables = null) =>
        ResolveInJson(json, lookup, unresolvedVariables, null);

    /// <summary>
    /// Resolves placeholders in all string values of a JSON document. Property names aren't changed.
    /// Returns the original string if no values changed.
    /// </summary>
    /// <param name="json">JSON document.</param>
    /// <param name="lookup">Function that returns the value of a variable or null if it isn't set.</param>
    /// <param name="unresolvedVariables">Collects names of variables that couldn't be resolved.</param>
    /// <param name="transformText">Optional transformation applied only to the text outside placeholders.</param>
    public static string ResolveInJson(string json, Func<string, string?> lookup, ICollection<string>? unresolvedVariables, Func<string, string>? transformText)
    {
        ArgumentNullException.ThrowIfNull(json);
        ArgumentNullException.ThrowIfNull(lookup);

        // string values may contain JSON escapes (eg. \u0024{NAME}) so only skip parsing when there are none
        if (transformText is null &&
            !ContainsPlaceholder(json) &&
            !json.Contains("\\u", StringComparison.Ordinal))
        {
            return json;
        }

        var node = JsonNode.Parse(json, documentOptions: ProxyUtils.JsonDocumentOptions);
        if (node is null)
        {
            return json;
        }

        var changed = false;
        node = ResolveInJsonNode(node, value =>
        {
            var resolved = Resolve(value, lookup, unresolvedVariables, transformText);
            changed |= !string.Equals(value, resolved, StringComparison.Ordinal);
            return resolved;
        });
        return changed ? node.ToJsonString(_writeOptions) : json;
    }

    /// <summary>
    /// Resolves placeholders in all string values of a JSON document using process environment variables.
    /// </summary>
    public static string ResolveInJson(string json, ICollection<string>? unresolvedVariables = null) =>
        ResolveInJson(json, Environment.GetEnvironmentVariable, unresolvedVariables);

    /// <summary>
    /// Resolves placeholders in all scalar values of a YAML document. Mapping keys aren't changed.
    /// Returns the original string if no values changed.
    /// </summary>
    /// <param name="yaml">YAML document.</param>
    /// <param name="lookup">Function that returns the value of a variable or null if it isn't set.</param>
    /// <param name="unresolvedVariables">Collects names of variables that couldn't be resolved.</param>
    /// <param name="transformText">Optional transformation applied only to the text outside placeholders.</param>
    public static string ResolveInYaml(string yaml, Func<string, string?> lookup, ICollection<string>? unresolvedVariables = null, Func<string, string>? transformText = null)
    {
        ArgumentNullException.ThrowIfNull(yaml);
        ArgumentNullException.ThrowIfNull(lookup);

        var stream = new YamlStream();
        using (var reader = new StringReader(yaml))
        {
            stream.Load(reader);
        }

        var changed = false;
        var visited = new HashSet<YamlNode>(ReferenceEqualityComparer.Instance);
        void resolveNode(YamlNode node)
        {
            // aliases point to the same node instance so each node is resolved once
            if (!visited.Add(node))
            {
                return;
            }

            switch (node)
            {
                case YamlScalarNode scalar when scalar.Value is not null:
                    var resolved = Resolve(scalar.Value, lookup, unresolvedVariables, transformText);
                    if (!string.Equals(scalar.Value, resolved, StringComparison.Ordinal))
                    {
                        scalar.Value = resolved;
                        changed = true;
                    }
                    break;
                case YamlMappingNode mapping:
                    foreach (var child in mapping.Children)
                    {
                        resolveNode(child.Value);
                    }
                    break;
                case YamlSequenceNode sequence:
                    foreach (var child in sequence.Children)
                    {
                        resolveNode(child);
                    }
                    break;
                default:
                    break;
            }
        }

        foreach (var document in stream.Documents)
        {
            resolveNode(document.RootNode);
        }

        if (!changed)
        {
            return yaml;
        }

        using var writer = new StringWriter();
        stream.Save(writer, assignAnchors: false);
        return writer.ToString();
    }

    /// <summary>
    /// Logs a warning for each environment variable that couldn't be resolved.
    /// </summary>
    public static void LogUnresolvedVariables(ILogger logger, IEnumerable<string> unresolvedVariables, string source)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(unresolvedVariables);

        foreach (var name in unresolvedVariables)
        {
            logger.LogWarning(
                "Environment variable {Name} referenced in {Source} isn't set. Leaving the placeholder as-is",
                name, source);
        }
    }

    private static JsonNode ResolveInJsonNode(JsonNode node, Func<string, string> resolve)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var property in obj.ToArray())
                {
                    if (property.Value is not null)
                    {
                        var resolved = ResolveInJsonNode(property.Value, resolve);
                        if (!ReferenceEquals(resolved, property.Value))
                        {
                            obj[property.Key] = resolved;
                        }
                    }
                }
                return obj;
            case JsonArray array:
                for (var i = 0; i < array.Count; i++)
                {
                    var item = array[i];
                    if (item is not null)
                    {
                        var resolved = ResolveInJsonNode(item, resolve);
                        if (!ReferenceEquals(resolved, item))
                        {
                            array[i] = resolved;
                        }
                    }
                }
                return array;
            case JsonValue value when value.GetValueKind() == JsonValueKind.String:
                var s = value.GetValue<string>();
                var r = resolve(s);
                return string.Equals(s, r, StringComparison.Ordinal) ? value : JsonValue.Create(r);
            default:
                return node;
        }
    }

    // ${env:NAME} accepts any characters except } so that names like ProgramFiles(x86) work;
    // bare ${NAME} is limited to identifiers to avoid matching unrelated ${...} text
    [GeneratedRegex(@"(?<escape>\$)?\$\{(?:env:(?<name>[^}]+)|(?<name>[A-Za-z_][A-Za-z0-9_]*))\}")]
    private static partial Regex PlaceholderRegex();
}
