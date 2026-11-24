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

        public void Visit(object obj, string path) => Visit(obj, null, path);

        // Returns the value to store in the parent: either obj itself or, when null
        // elements were removed from an array or list, a filtered copy of it.
        private object Visit(object obj, NullabilityInfo? nullability, string path)
        {
            var type = obj.GetType();
            if (!ShouldInspect(type) || !_visited.Add(obj))
            {
                return obj;
            }

            if (obj is IDictionary dictionary)
            {
                VisitDictionary(dictionary, nullability, path);
                return obj;
            }

            if (obj is IEnumerable enumerable)
            {
                return VisitCollection(obj, enumerable, nullability, path);
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
                var canSet = property.SetMethod is { IsPublic: true };
                var newValue = Visit(value, canSet ? _nullabilityContext.Create(property) : null, propertyPath);
                if (!ReferenceEquals(newValue, value))
                {
                    property.SetValue(obj, newValue);
                }
            }

            return obj;
        }

        private void VisitDictionary(IDictionary dictionary, NullabilityInfo? nullability, string path)
        {
            var type = dictionary.GetType();
            var valueType = type.IsGenericType ? type.GetGenericArguments().LastOrDefault() : null;
            var valueNullability = nullability?.GenericTypeArguments.Length == 2 ? nullability.GenericTypeArguments[1] : null;
            var removeNulls = !dictionary.IsReadOnly && IsNonNullableReference(valueType, valueNullability);

            foreach (var key in dictionary.Keys.Cast<object>().ToArray())
            {
                var value = dictionary[key];
                if (value is null)
                {
                    if (removeNulls)
                    {
                        dictionary.Remove(key);
                        removed.Add($"{path}.{key}");
                    }
                    continue;
                }

                var newValue = Visit(value, dictionary.IsReadOnly ? null : valueNullability, $"{path}.{key}");
                if (!ReferenceEquals(newValue, value))
                {
                    dictionary[key] = newValue;
                }
            }
        }

        private object VisitCollection(object obj, IEnumerable enumerable, NullabilityInfo? nullability, string path)
        {
            var type = obj.GetType();
            Type? elementType = null;
            NullabilityInfo? elementNullability = null;
            if (type.IsArray)
            {
                elementType = type.GetElementType();
                // The declared type might be a generic interface, like IEnumerable<T>
                elementNullability = nullability?.ElementType ??
                    (nullability?.GenericTypeArguments.Length == 1 ? nullability.GenericTypeArguments[0] : null);
            }
            else if (type.IsGenericType && type.GetGenericArguments().Length == 1)
            {
                elementType = type.GetGenericArguments()[0];
                elementNullability = nullability?.GenericTypeArguments.Length == 1 ? nullability.GenericTypeArguments[0] : null;
            }

            // Visit the original items so that reported paths use the indexes from
            // the file rather than from the filtered collection.
            var items = enumerable.Cast<object?>().ToArray();
            var canModify = type.IsArray || obj is IList { IsReadOnly: false, IsFixedSize: false };
            var removeNulls = canModify && IsNonNullableReference(elementType, elementNullability);
            var changed = false;
            var kept = new List<object?>(items.Length);
            for (var i = 0; i < items.Length; i++)
            {
                var item = items[i];
                if (item is null)
                {
                    if (removeNulls)
                    {
                        removed.Add($"{path}[{i}]");
                        changed = true;
                    }
                    else
                    {
                        kept.Add(item);
                    }
                    continue;
                }

                var newItem = Visit(item, canModify ? elementNullability : null, $"{path}[{i}]");
                changed |= !ReferenceEquals(newItem, item);
                kept.Add(newItem);
            }

            if (!changed)
            {
                return obj;
            }

            if (type.IsArray)
            {
                var array = Array.CreateInstance(elementType!, kept.Count);
                for (var i = 0; i < kept.Count; i++)
                {
                    array.SetValue(kept[i], i);
                }
                return array;
            }

            var list = (IList)obj;
            list.Clear();
            foreach (var item in kept)
            {
                _ = list.Add(item);
            }
            return obj;
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
