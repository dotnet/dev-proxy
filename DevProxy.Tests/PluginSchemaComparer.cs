// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Microsoft.Extensions.Configuration;
using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace DevProxy.Tests;

internal enum SchemaMismatchKind
{
    MissingInSchema,
    MissingInClass,
    Type,
    Enum,
    Default,
    MissingDefault,
    Required,
}

internal sealed record SchemaMismatch(string Plugin, string Property, SchemaMismatchKind Kind, string Details)
{
    public override string ToString() => $"{Plugin}: property '{Property}' {Kind}: {Details}";
}

/// <summary>
/// Compares a plugin configuration class with the plugin's JSON schema the same way
/// configuration binding sees it: public settable (or bindable get-only) properties,
/// named with the camelCase policy or <see cref="ConfigurationKeyNameAttribute"/>.
/// </summary>
internal static class PluginSchemaComparer
{
    private static readonly JsonSerializerOptions _serializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private static readonly NullabilityInfoContext _nullability = new();

    public static IReadOnlyList<SchemaMismatch> Compare(string plugin, Type configurationType, JsonObject schema)
    {
        ArgumentNullException.ThrowIfNull(configurationType);
        ArgumentNullException.ThrowIfNull(schema);

        var mismatches = new List<SchemaMismatch>();
        var schemaProperties = schema["properties"] as JsonObject ?? [];
        var instance = Activator.CreateInstance(configurationType);
        var classProperties = GetBindableProperties(configurationType);

        foreach (var (name, property) in classProperties)
        {
            if (schemaProperties[name] is not JsonObject schemaProperty)
            {
                mismatches.Add(new(plugin, name, SchemaMismatchKind.MissingInSchema,
                    $"exists on {configurationType.Name} but not in the schema"));
                continue;
            }

            var resolved = Resolve(schema, schemaProperty);
            CompareType(plugin, name, property.PropertyType, schema, resolved, mismatches);
            CompareDefault(plugin, name, property, instance, resolved, mismatches);
        }

        foreach (var (name, _) in schemaProperties)
        {
            if (name == "$schema" || classProperties.ContainsKey(name))
            {
                continue;
            }

            mismatches.Add(new(plugin, name, SchemaMismatchKind.MissingInClass,
                $"exists in the schema but not on {configurationType.Name}"));
        }

        if (schema["required"] is JsonArray required)
        {
            foreach (var name in required.Select(r => r?.GetValue<string>()).OfType<string>())
            {
                if (classProperties.TryGetValue(name, out var property) && IsNullable(property))
                {
                    mismatches.Add(new(plugin, name, SchemaMismatchKind.Required,
                        $"is required in the schema but nullable on {configurationType.Name}"));
                }
            }
        }

        return mismatches;
    }

    public static Dictionary<string, PropertyInfo> GetBindableProperties(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);

        var result = new Dictionary<string, PropertyInfo>(StringComparer.Ordinal);
        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.GetMethod is null || property.GetIndexParameters().Length > 0)
            {
                continue;
            }

            // Configuration binding sets public setters and populates get-only
            // auto-properties of complex types (e.g. `public List<string> X { get; } = []`).
            var bindable = property.SetMethod?.IsPublic == true ||
                (IsComplex(property.PropertyType) && IsAutoProperty(property));
            if (!bindable)
            {
                continue;
            }

            var name = property.GetCustomAttribute<ConfigurationKeyNameAttribute>()?.Name ??
                JsonNamingPolicy.CamelCase.ConvertName(property.Name);
            result[name] = property;
        }

        return result;
    }

    private static void CompareType(string plugin, string name, Type type, JsonObject root, JsonObject schemaProperty, List<SchemaMismatch> mismatches)
    {
        var expected = GetJsonTypes(type);
        if (expected is null)
        {
            return;
        }

        var actual = GetSchemaTypes(root, schemaProperty);
        if (actual.Count == 0)
        {
            return;
        }

        if (!actual.Overlaps(expected))
        {
            mismatches.Add(new(plugin, name, SchemaMismatchKind.Type,
                $"is {string.Join("|", expected)} ({FriendlyName(type)}) but the schema says {string.Join("|", actual)}"));
            return;
        }

        var underlying = Nullable.GetUnderlyingType(type) ?? type;
        if (underlying.IsEnum)
        {
            CompareEnum(plugin, name, underlying, schemaProperty, mismatches);
        }
        else if (expected.Contains("array") && GetElementType(underlying) is { } elementType &&
            schemaProperty["items"] is JsonObject items)
        {
            CompareType(plugin, $"{name}[]", elementType, root, Resolve(root, items), mismatches);
        }
    }

    private static void CompareEnum(string plugin, string name, Type enumType, JsonObject schemaProperty, List<SchemaMismatch> mismatches)
    {
        var enumValues = GetEnumValues(schemaProperty);
        if (enumValues is null)
        {
            mismatches.Add(new(plugin, name, SchemaMismatchKind.Enum,
                $"is enum {enumType.Name} but the schema doesn't restrict values with 'enum'"));
            return;
        }

        // Configuration binding parses enums case-insensitively.
        var members = Enum.GetNames(enumType);
        var notInClass = enumValues.Where(v => !members.Contains(v, StringComparer.OrdinalIgnoreCase)).ToArray();
        var notInSchema = members.Where(m => !enumValues.Contains(m, StringComparer.OrdinalIgnoreCase)).ToArray();
        if (notInClass.Length > 0)
        {
            mismatches.Add(new(plugin, name, SchemaMismatchKind.Enum,
                $"schema allows [{string.Join(", ", notInClass)}] which enum {enumType.Name} doesn't define"));
        }

        if (notInSchema.Length > 0)
        {
            mismatches.Add(new(plugin, name, SchemaMismatchKind.Enum,
                $"enum {enumType.Name} defines [{string.Join(", ", notInSchema)}] which the schema doesn't allow"));
        }
    }

    private static void CompareDefault(string plugin, string name, PropertyInfo property, object? instance, JsonObject schemaProperty, List<SchemaMismatch> mismatches)
    {
        var value = property.GetValue(instance);
        var underlying = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
        var actual = JsonSerializer.SerializeToNode(value, property.PropertyType, _serializerOptions);

        if (!schemaProperty.TryGetPropertyValue("default", out var schemaDefault))
        {
            if (IsSimpleOrSimpleCollection(underlying) && !IsTrivialDefault(value))
            {
                mismatches.Add(new(plugin, name, SchemaMismatchKind.MissingDefault,
                    $"defaults to {Format(actual)} but the schema doesn't declare 'default'"));
            }

            return;
        }

        var equal = underlying.IsEnum && actual is JsonValue && schemaDefault is JsonValue
            ? string.Equals(actual.GetValue<string>(), schemaDefault.GetValue<object>()?.ToString(), StringComparison.OrdinalIgnoreCase)
            : JsonNode.DeepEquals(actual, schemaDefault);
        if (!equal)
        {
            mismatches.Add(new(plugin, name, SchemaMismatchKind.Default,
                $"defaults to {Format(actual)} but the schema default is {Format(schemaDefault)}"));
        }
    }

    private static HashSet<string>? GetJsonTypes(Type type)
    {
        var underlying = Nullable.GetUnderlyingType(type) ?? type;

        if (underlying == typeof(object) || typeof(JsonNode).IsAssignableFrom(underlying) ||
            underlying == typeof(JsonElement) || typeof(IConfiguration).IsAssignableFrom(underlying))
        {
            return null;
        }

        if (underlying == typeof(string) || underlying == typeof(char) || underlying == typeof(Guid) ||
            underlying == typeof(Uri) || underlying == typeof(DateTime) || underlying == typeof(DateTimeOffset) ||
            underlying == typeof(TimeSpan) || underlying.IsEnum)
        {
            return ["string"];
        }

        if (underlying == typeof(bool))
        {
            return ["boolean"];
        }

        if (underlying == typeof(byte) || underlying == typeof(sbyte) || underlying == typeof(short) ||
            underlying == typeof(ushort) || underlying == typeof(int) || underlying == typeof(uint) ||
            underlying == typeof(long) || underlying == typeof(ulong))
        {
            return ["integer"];
        }

        if (underlying == typeof(float) || underlying == typeof(double) || underlying == typeof(decimal))
        {
            // An integer-only schema is narrower than the class but still binds.
            return ["number", "integer"];
        }

        if (IsDictionary(underlying))
        {
            return ["object"];
        }

        if (typeof(IEnumerable).IsAssignableFrom(underlying))
        {
            return ["array"];
        }

        return ["object"];
    }

    private static HashSet<string> GetSchemaTypes(JsonObject root, JsonObject schemaProperty)
    {
        var types = new HashSet<string>(StringComparer.Ordinal);
        switch (schemaProperty["type"])
        {
            case JsonValue value:
                _ = types.Add(value.GetValue<string>());
                break;
            case JsonArray array:
                types.UnionWith(array.Select(t => t!.GetValue<string>()));
                break;
            default:
                foreach (var keyword in new[] { "oneOf", "anyOf" })
                {
                    if (schemaProperty[keyword] is JsonArray branches)
                    {
                        foreach (var branch in branches.OfType<JsonObject>())
                        {
                            types.UnionWith(GetSchemaTypes(root, Resolve(root, branch)));
                        }
                    }
                }
                break;
        }

        // "integer" values are also valid "number" values.
        if (types.Contains("integer"))
        {
            _ = types.Add("number");
        }

        _ = types.Remove("null");
        return types;
    }

    private static List<string>? GetEnumValues(JsonObject schemaProperty)
    {
        if (schemaProperty["enum"] is JsonArray values)
        {
            return [.. values.Select(v => v?.ToString()).OfType<string>()];
        }

        foreach (var keyword in new[] { "oneOf", "anyOf" })
        {
            if (schemaProperty[keyword] is JsonArray branches &&
                branches.OfType<JsonObject>().All(b => b.ContainsKey("const")))
            {
                return [.. branches.Select(b => b!["const"]?.ToString()).OfType<string>()];
            }
        }

        return null;
    }

    private static JsonObject Resolve(JsonObject root, JsonObject schemaProperty)
    {
        var current = schemaProperty;
        for (var i = 0; i < 10 && current["$ref"] is JsonValue reference; i++)
        {
            var path = reference.GetValue<string>();
            if (!path.StartsWith("#/", StringComparison.Ordinal))
            {
                break;
            }

            JsonNode? node = root;
            foreach (var segment in path[2..].Split('/'))
            {
                node = node?[segment.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal)];
            }

            if (node is not JsonObject target)
            {
                break;
            }

            current = target;
        }

        return current;
    }

    private static bool IsNullable(PropertyInfo property)
    {
        if (property.PropertyType.IsValueType)
        {
            return Nullable.GetUnderlyingType(property.PropertyType) is not null;
        }

        return _nullability.Create(property).WriteState != NullabilityState.NotNull;
    }

    private static bool IsComplex(Type type) =>
        !type.IsValueType && type != typeof(string) && type != typeof(Uri);

    private static bool IsAutoProperty(PropertyInfo property) =>
        property.GetMethod?.GetCustomAttribute<CompilerGeneratedAttribute>() is not null;

    private static bool IsDictionary(Type type) =>
        typeof(IDictionary).IsAssignableFrom(type) ||
        type.GetInterfaces().Append(type).Any(i => i.IsGenericType &&
            (i.GetGenericTypeDefinition() == typeof(IDictionary<,>) ||
             i.GetGenericTypeDefinition() == typeof(IReadOnlyDictionary<,>)));

    private static Type? GetElementType(Type type)
    {
        if (type.IsArray)
        {
            return type.GetElementType();
        }

        return type.GetInterfaces().Append(type)
            .FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>))?
            .GetGenericArguments()[0];
    }

    private static bool IsSimple(Type type)
    {
        var underlying = Nullable.GetUnderlyingType(type) ?? type;
        return underlying.IsPrimitive || underlying.IsEnum || underlying == typeof(string) ||
            underlying == typeof(decimal) || underlying == typeof(Uri);
    }

    private static bool IsSimpleOrSimpleCollection(Type type) =>
        IsSimple(type) ||
        (!IsDictionary(type) && typeof(IEnumerable).IsAssignableFrom(type) &&
            GetElementType(type) is { } elementType && IsSimple(elementType));

    private static bool IsTrivialDefault(object? value) => value switch
    {
        null => true,
        string s => s.Length == 0,
        bool b => !b,
        Enum => false,
        IEnumerable e => !e.Cast<object>().Any(),
        IConvertible c => c.ToDecimal(CultureInfo.InvariantCulture) == 0,
        _ => false,
    };

    private static string Format(JsonNode? node) => node?.ToJsonString() ?? "null";

    private static string FriendlyName(Type type)
    {
        if (Nullable.GetUnderlyingType(type) is { } underlying)
        {
            return FriendlyName(underlying) + "?";
        }

        return type.IsGenericType
            ? $"{type.Name[..type.Name.IndexOf('`', StringComparison.Ordinal)]}<{string.Join(", ", type.GetGenericArguments().Select(FriendlyName))}>"
            : type.Name;
    }
}
