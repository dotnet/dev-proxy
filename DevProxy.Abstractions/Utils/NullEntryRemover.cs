// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace DevProxy.Abstractions.Utils;

/// <summary>
/// Removes <c>null</c> entries from collections and dictionaries in a deserialized
/// object graph, where the element type is declared as non-nullable. Data files can
/// contain entries like <c>"errors": [null]</c>, which deserialize to null elements
/// that consumers don't expect.
/// </summary>
public static class NullEntryRemover
{
    /// <summary>
    /// Removes null entries from non-nullable collections and dictionaries in the
    /// object graph rooted at <paramref name="root"/>.
    /// </summary>
    /// <returns>JSON paths (for example <c>errors[0]</c>) of the removed entries.</returns>
    public static IReadOnlyList<string> RemoveNullEntries(object? root, JsonNamingPolicy? namingPolicy = null)
    {
        var removed = new List<string>();
        if (root is not null)
        {
            new Walker(namingPolicy ?? ProxyUtils.JsonSerializerOptions.PropertyNamingPolicy, removed).Visit(root, "$");
        }
        return removed;
    }

    private sealed class Walker(JsonNamingPolicy? namingPolicy, List<string> removed)
    {
        private readonly NullabilityInfoContext _nullabilityContext = new();
        private readonly HashSet<object> _visited = new(ReferenceEqualityComparer.Instance);

        public void Visit(object obj, string path)
        {
            var type = obj.GetType();
            if (!ShouldInspect(type) || !_visited.Add(obj))
            {
                return;
            }

            if (obj is IDictionary dictionary)
            {
                foreach (var key in dictionary.Keys.Cast<object>().ToArray())
                {
                    if (dictionary[key] is { } value)
                    {
                        Visit(value, $"{path}.{key}");
                    }
                }
                return;
            }

            if (obj is IEnumerable enumerable)
            {
                var index = 0;
                foreach (var item in enumerable)
                {
                    if (item is not null)
                    {
                        Visit(item, $"{path}[{index}]");
                    }
                    index++;
                }
                return;
            }

            foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!property.CanRead ||
                    property.GetIndexParameters().Length > 0 ||
                    property.GetCustomAttribute<JsonIgnoreAttribute>() is not null)
                {
                    continue;
                }

                var value = property.GetValue(obj);
                if (value is null)
                {
                    continue;
                }

                var propertyPath = path == "$" ? GetJsonName(property) : $"{path}.{GetJsonName(property)}";
                var nullability = _nullabilityContext.Create(property);
                if (value is IDictionary dictionaryValue)
                {
                    RemoveNullValues(dictionaryValue, property, nullability, propertyPath);
                }
                else if (value is IEnumerable enumerableValue && value is not string)
                {
                    // Visit the original collection so that reported paths use the
                    // indexes from the file rather than from the filtered collection.
                    RemoveNullElements(obj, enumerableValue, property, nullability, propertyPath);
                }

                Visit(value, propertyPath);
            }
        }

        private void RemoveNullValues(IDictionary dictionary, PropertyInfo property, NullabilityInfo nullability, string path)
        {
            var valueType = property.PropertyType.IsGenericType ? property.PropertyType.GetGenericArguments().LastOrDefault() : null;
            var valueNullability = nullability.GenericTypeArguments.Length == 2 ? nullability.GenericTypeArguments[1] : null;
            if (dictionary.IsReadOnly || !IsNonNullableReference(valueType, valueNullability))
            {
                return;
            }

            foreach (var key in dictionary.Keys.Cast<object>().ToArray())
            {
                if (dictionary[key] is null)
                {
                    dictionary.Remove(key);
                    removed.Add($"{path}.{key}");
                }
            }
        }

        private void RemoveNullElements(object owner, IEnumerable enumerable, PropertyInfo property, NullabilityInfo nullability, string path)
        {
            var propertyType = property.PropertyType;
            Type? elementType;
            NullabilityInfo? elementNullability;
            if (propertyType.IsArray)
            {
                elementType = propertyType.GetElementType();
                elementNullability = nullability.ElementType;
            }
            else if (propertyType.IsGenericType && propertyType.GetGenericArguments().Length == 1)
            {
                elementType = propertyType.GetGenericArguments()[0];
                elementNullability = nullability.GenericTypeArguments.Length == 1 ? nullability.GenericTypeArguments[0] : null;
            }
            else
            {
                return;
            }

            if (!IsNonNullableReference(elementType, elementNullability) ||
                property.SetMethod is null || !property.SetMethod.IsPublic)
            {
                return;
            }

            var items = enumerable.Cast<object?>().ToArray();
            if (!items.Contains(null))
            {
                return;
            }

            var listType = typeof(List<>).MakeGenericType(elementType!);
            object filtered;
            if (propertyType.IsArray)
            {
                filtered = Array.CreateInstance(elementType!, items.Count(i => i is not null));
            }
            else if (propertyType.IsAssignableFrom(listType))
            {
                filtered = Activator.CreateInstance(listType)!;
            }
            else
            {
                return;
            }

            var target = 0;
            for (var i = 0; i < items.Length; i++)
            {
                if (items[i] is null)
                {
                    removed.Add($"{path}[{i}]");
                    continue;
                }

                if (filtered is Array array)
                {
                    array.SetValue(items[i], target++);
                }
                else
                {
                    ((IList)filtered).Add(items[i]);
                }
            }

            property.SetValue(owner, filtered);
        }

        private string GetJsonName(PropertyInfo property) =>
            property.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name ??
            namingPolicy?.ConvertName(property.Name) ??
            property.Name;

        private static bool IsNonNullableReference(Type? type, NullabilityInfo? nullability) =>
            type is not null &&
            !type.IsValueType &&
            nullability?.ReadState == NullabilityState.NotNull;

        // Skip framework types, as well as raw JSON values (for example, mock
        // response bodies), where null is a legitimate value.
        private static bool ShouldInspect(Type type)
        {
            if (type.IsPrimitive || type.IsEnum || type == typeof(string) ||
                typeof(JsonNode).IsAssignableFrom(type) || type == typeof(JsonElement) || type == typeof(JsonDocument))
            {
                return false;
            }

            if (type.IsArray || (type.IsGenericType && typeof(IEnumerable).IsAssignableFrom(type) && type.Namespace?.StartsWith("System.Collections", StringComparison.Ordinal) == true))
            {
                return true;
            }

            var ns = type.Namespace ?? string.Empty;
            return !ns.StartsWith("System", StringComparison.Ordinal) &&
                !ns.StartsWith("Microsoft", StringComparison.Ordinal) &&
                !ns.StartsWith("Newtonsoft", StringComparison.Ordinal);
        }
    }
}
