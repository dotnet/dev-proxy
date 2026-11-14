// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Microsoft.OpenApi;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DevProxy.Plugins.Mocking;

/// <summary>
/// Generates sample values and simplified, reference-free descriptions
/// of OpenAPI schemas.
/// </summary>
internal static class OpenApiSchemaSampleGenerator
{
    private const int MaxDepth = 8;

    public static JsonNode? GenerateSample(IOpenApiSchema? schema) => GenerateSample(schema, 0, []);

    /// <summary>
    /// Returns a simplified JSON Schema-like representation of the schema with
    /// all references resolved, suitable to pass to a language model.
    /// </summary>
    public static JsonObject Describe(IOpenApiSchema? schema) => Describe(schema, 0, []) ?? [];

    public static bool IsStringSchema(IOpenApiSchema? schema) =>
        schema is not null && Describe(schema)["type"]?.GetValue<string>() == "string";

    private static string? GetReferenceId(IOpenApiSchema schema) =>
        schema is OpenApiSchemaReference reference ? reference.Reference?.ReferenceV3 : null;

    private static JsonNode? GenerateSample(IOpenApiSchema? schema, int depth, HashSet<string> visiting)
    {
        if (schema is null || depth > MaxDepth)
        {
            return null;
        }

        // stop at recursive references to avoid deeply nested samples
        var referenceId = GetReferenceId(schema);
        if (referenceId is not null && !visiting.Add(referenceId))
        {
            return null;
        }

        try
        {
            return GenerateSampleCore(schema, depth, visiting);
        }
        finally
        {
            if (referenceId is not null)
            {
                _ = visiting.Remove(referenceId);
            }
        }
    }

    private static JsonNode? GenerateSampleCore(IOpenApiSchema schema, int depth, HashSet<string> visiting)
    {
        // Example is obsolete in favor of Examples, but OpenAPI 3.0 specs still use it
#pragma warning disable CS0618
        if (schema.Example is not null)
        {
            return schema.Example.DeepClone();
        }
#pragma warning restore CS0618

        var example = schema.Examples?.FirstOrDefault(e => e is not null);
        if (example is not null)
        {
            return example.DeepClone();
        }

        if (schema.Default is not null)
        {
            return schema.Default.DeepClone();
        }

        var enumValue = schema.Enum?.FirstOrDefault(e => e is not null);
        if (enumValue is not null)
        {
            return enumValue.DeepClone();
        }

        if (schema.AllOf is { Count: > 0 })
        {
            return GenerateAllOfSample(schema, depth, visiting);
        }

        var alternative = schema.OneOf?.FirstOrDefault() ?? schema.AnyOf?.FirstOrDefault();
        if (alternative is not null)
        {
            var alternativeSample = GenerateSample(alternative, depth + 1, visiting);
            // keep the properties defined next to oneOf/anyOf
            if (schema.Properties is { Count: > 0 } && alternativeSample is null or JsonObject)
            {
                var obj = GenerateObjectSample(schema, depth, visiting);
                if (alternativeSample is JsonObject alternativeObject)
                {
                    MergeInto(obj, alternativeObject);
                }
                return obj;
            }
            return alternativeSample;
        }

        return GetEffectiveType(schema) switch
        {
            JsonSchemaType.Object => GenerateObjectSample(schema, depth, visiting),
            JsonSchemaType.Array => GenerateArraySample(schema, depth, visiting),
            JsonSchemaType.Integer => JsonValue.Create(GetNumber(schema, isInteger: true)),
            JsonSchemaType.Number => JsonValue.Create(GetNumber(schema, isInteger: false)),
            JsonSchemaType.Boolean => JsonValue.Create(true),
            JsonSchemaType.Null => null,
            _ => JsonValue.Create(GetStringSample(schema))
        };
    }

    private static JsonNode? GenerateAllOfSample(IOpenApiSchema schema, int depth, HashSet<string> visiting)
    {
        JsonObject? merged = null;

        foreach (var subSchema in schema.AllOf ?? [])
        {
            if (GenerateSample(subSchema, depth + 1, visiting) is JsonObject obj)
            {
                merged ??= [];
                MergeInto(merged, obj);
            }
        }

        if (schema.Properties is { Count: > 0 })
        {
            merged ??= [];
            MergeInto(merged, GenerateObjectSample(schema, depth, visiting));
        }

        if (merged is not null)
        {
            return merged;
        }

        // for non-object values, combine the constraints of all branches
        // so that the value satisfies all of them
        return GenerateSample(CombineConstraints(FlattenAllOf(schema, depth)), depth + 1, visiting);
    }

    private static IEnumerable<IOpenApiSchema> FlattenAllOf(IOpenApiSchema schema, int depth)
    {
        yield return schema;

        if (depth > MaxDepth)
        {
            yield break;
        }

        foreach (var subSchema in schema.AllOf ?? [])
        {
            if (subSchema is null)
            {
                continue;
            }

            foreach (var nested in FlattenAllOf(subSchema, depth + 1))
            {
                yield return nested;
            }
        }
    }

    private static OpenApiSchema CombineConstraints(IEnumerable<IOpenApiSchema?> schemas)
    {
        var combined = new OpenApiSchema();
        decimal? minimum = null, maximum = null;

        foreach (var schema in schemas)
        {
            if (schema is null)
            {
                continue;
            }

#pragma warning disable CS0618
            combined.Example ??= schema.Example?.DeepClone();
#pragma warning restore CS0618
            combined.Default ??= schema.Default?.DeepClone();
            if (combined.Enum is not { Count: > 0 } && schema.Enum is { Count: > 0 })
            {
                combined.Enum = [.. schema.Enum];
            }
            combined.Type ??= schema.Type;
            combined.Format ??= schema.Format;
            combined.Items ??= schema.Items;

            if (TryParseBound(schema.Minimum?.ToString(), out var schemaMinimum) && (minimum is null || schemaMinimum > minimum))
            {
                minimum = schemaMinimum;
            }
            if (TryParseBound(schema.Maximum?.ToString(), out var schemaMaximum) && (maximum is null || schemaMaximum < maximum))
            {
                maximum = schemaMaximum;
            }
            if (schema.MinLength is int minLength && (combined.MinLength is null || minLength > combined.MinLength))
            {
                combined.MinLength = minLength;
            }
            if (schema.MaxLength is int maxLength && (combined.MaxLength is null || maxLength < combined.MaxLength))
            {
                combined.MaxLength = maxLength;
            }
            if (schema.MinItems is int minItems && (combined.MinItems is null || minItems > combined.MinItems))
            {
                combined.MinItems = minItems;
            }
            if (schema.MaxItems is int maxItems && (combined.MaxItems is null || maxItems < combined.MaxItems))
            {
                combined.MaxItems = maxItems;
            }
            if (schema.UniqueItems == true)
            {
                combined.UniqueItems = true;
            }
            combined.Pattern ??= schema.Pattern;
            if (schema.MultipleOf is decimal multipleOf && multipleOf > 0)
            {
                combined.MultipleOf = combined.MultipleOf is decimal current ? LeastCommonMultiple(current, multipleOf) : multipleOf;
            }
        }

        combined.Minimum = minimum?.ToString(CultureInfo.InvariantCulture);
        combined.Maximum = maximum?.ToString(CultureInfo.InvariantCulture);
        return combined;
    }

    private static JsonObject GenerateObjectSample(IOpenApiSchema schema, int depth, HashSet<string> visiting)
    {
        var obj = new JsonObject();
        foreach (var (name, propertySchema) in schema.Properties ?? new Dictionary<string, IOpenApiSchema>())
        {
            if (propertySchema is null || propertySchema.WriteOnly)
            {
                continue;
            }

            obj[name] = GenerateSample(propertySchema, depth + 1, visiting);
        }

        if (obj.Count == 0 && schema.AdditionalProperties is not null)
        {
            obj["additionalProp1"] = GenerateSample(schema.AdditionalProperties, depth + 1, visiting);
        }

        return obj;
    }

    private static JsonArray GenerateArraySample(IOpenApiSchema schema, int depth, HashSet<string> visiting)
    {
        var item = GenerateSample(schema.Items, depth + 1, visiting);
        // null is a valid item when the item schema allows only null,
        // otherwise it's a recursion cut-off
        if (item is null && (schema.Items is null || GetEffectiveType(schema.Items) != JsonSchemaType.Null))
        {
            return [];
        }

        var count = Math.Max(schema.MinItems ?? 1, 1);
        if (schema.MaxItems is int maxItems)
        {
            count = Math.Min(count, Math.Max(maxItems, 0));
        }

        var array = new JsonArray();
        if (schema.UniqueItems != true)
        {
            for (var i = 0; i < count; i++)
            {
                array.Add(item?.DeepClone());
            }
            return array;
        }

        // best-effort: vary the sample to get distinct items. If we can't,
        // return fewer items rather than duplicates
        if (count == 0)
        {
            return array;
        }
        array.Add(item?.DeepClone());
        for (var attempt = 1; array.Count < count && attempt < count * 4; attempt++)
        {
            var variant = CreateVariant(item, schema.Items, attempt, depth);
            if (variant is not null && !array.Any(i => JsonNode.DeepEquals(i, variant)))
            {
                array.Add(variant);
            }
        }
        return array;
    }

    private static JsonNode? CreateVariant(JsonNode? sample, IOpenApiSchema? schema, int index, int depth)
    {
        if (schema is null || sample is null || depth > MaxDepth)
        {
            return null;
        }

        var constraints = schema.AllOf is { Count: > 0 } ? CombineConstraints(FlattenAllOf(schema, depth)) : schema;
        var enumValues = constraints.Enum?.Where(e => e is not null).ToArray();
        if (enumValues is { Length: > 0 })
        {
            return index < enumValues.Length ? enumValues[index]!.DeepClone() : null;
        }

        if (sample is JsonObject obj)
        {
            foreach (var (name, value) in obj)
            {
                if (value is not JsonValue || FindPropertySchema(schema, name, depth) is not IOpenApiSchema propertySchema)
                {
                    continue;
                }

                var variant = CreateVariant(value, propertySchema, index, depth + 1);
                if (variant is not null)
                {
                    var clone = obj.DeepClone().AsObject();
                    clone[name] = variant;
                    return clone;
                }
            }
            return null;
        }

        if (sample is not JsonValue scalar)
        {
            return null;
        }

        switch (scalar.GetValueKind())
        {
            case JsonValueKind.Number when scalar.TryGetValue<decimal>(out var number):
                return CreateNumberVariant(number, constraints, index);
            case JsonValueKind.True or JsonValueKind.False when index == 1:
                return JsonValue.Create(scalar.GetValueKind() == JsonValueKind.False);
            case JsonValueKind.String when scalar.TryGetValue<string>(out var text):
                return CreateStringVariant(text, constraints, index) is string variant ? JsonValue.Create(variant) : null;
            default:
                return null;
        }
    }

    private static JsonValue? CreateNumberVariant(decimal number, IOpenApiSchema schema, int index)
    {
        try
        {
            var isInteger = GetEffectiveType(schema) == JsonSchemaType.Integer;
            var step = schema.MultipleOf > 0 ? schema.MultipleOf.Value : 1m;
            if (isInteger)
            {
                step = GetIntegerStep(step);
            }

            var hasMinimum = TryParseBound(schema.Minimum?.ToString(), out var minimum);
            var hasMaximum = TryParseBound(schema.Maximum?.ToString(), out var maximum);

            var up = number + (index * step);
            if (!hasMaximum || up <= maximum)
            {
                return JsonValue.Create(up);
            }
            var down = number - (index * step);
            return !hasMinimum || down >= minimum ? JsonValue.Create(down) : null;
        }
        catch (OverflowException)
        {
            return null;
        }
    }

    private static string? CreateStringVariant(string text, IOpenApiSchema schema, int index)
    {
        var suffix = index.ToString(CultureInfo.InvariantCulture);
        switch (schema.Format?.ToLowerInvariant())
        {
            case "uuid" when text.Length > 0:
                return text[..^1] + (index % 16).ToString("x", CultureInfo.InvariantCulture);
            case "email" or "idn-email" when text.Contains('@', StringComparison.Ordinal):
                var at = text.IndexOf('@', StringComparison.Ordinal);
                return text[..at] + suffix + text[at..];
            case null or "":
                if (schema.MaxLength is int maxLength && text.Length + suffix.Length > maxLength)
                {
                    return maxLength >= suffix.Length ? text[..(maxLength - suffix.Length)] + suffix : null;
                }
                return text + suffix;
            default:
                // other formats, like dates, can't be varied safely
                return null;
        }
    }

    private static IOpenApiSchema? FindPropertySchema(IOpenApiSchema schema, string name, int depth)
    {
        foreach (var candidate in FlattenAllOf(schema, depth))
        {
            if (candidate.Properties?.TryGetValue(name, out var propertySchema) == true)
            {
                return propertySchema;
            }
        }

        var alternative = schema.OneOf?.FirstOrDefault() ?? schema.AnyOf?.FirstOrDefault();
        return alternative is not null && depth <= MaxDepth ? FindPropertySchema(alternative, name, depth + 1) : null;
    }

    private static string GetStringSample(IOpenApiSchema schema)
    {
        var value = schema.Format?.ToLowerInvariant() switch
        {
            "date-time" => "2024-01-01T12:00:00Z",
            "date" => "2024-01-01",
            "time" => "12:00:00",
            "duration" => "P1D",
            "uuid" => "3fa85f64-5717-4562-b3fc-2c963f66afa6",
            "email" or "idn-email" => "user@example.com",
            "uri" or "url" or "iri" or "uri-reference" or "iri-reference" => "https://example.com",
            "hostname" or "idn-hostname" => "example.com",
            "ipv4" => "192.168.0.1",
            "ipv6" => "2001:db8::1",
            "byte" => "c3RyaW5n",
            "password" => "P@ssw0rd!",
            _ => "string"
        };

        if (schema.MinLength is int minLength && value.Length < minLength)
        {
            value = value.PadRight(minLength, 'x');
        }
        if (schema.MaxLength is int maxLength && value.Length > maxLength)
        {
            value = value[..maxLength];
        }

        return value;
    }

    private static decimal GetNumber(IOpenApiSchema schema, bool isInteger)
    {
        var multipleOf = schema.MultipleOf > 0 ? schema.MultipleOf : null;

        // use 0 unless it's outside the allowed range. 0 is a multiple of any number
        if (TryParseBound(schema.Minimum?.ToString(), out var minimum) && minimum > 0)
        {
            var value = isInteger ? decimal.Ceiling(minimum) : minimum;
            return multipleOf is decimal step ? RoundUpToMultiple(value, step, isInteger) : value;
        }
        if (TryParseBound(schema.Maximum?.ToString(), out var maximum) && maximum < 0)
        {
            var value = isInteger ? decimal.Floor(maximum) : maximum;
            return multipleOf is decimal step ? -RoundUpToMultiple(-value, step, isInteger) : value;
        }

        return 0;
    }

    private static decimal RoundUpToMultiple(decimal value, decimal step, bool isInteger)
    {
        try
        {
            if (isInteger)
            {
                step = GetIntegerStep(step);
            }
            return decimal.Ceiling(value / step) * step;
        }
        catch (OverflowException)
        {
            return value;
        }
    }

    // the smallest integer that's a multiple of step. For step p/q in lowest
    // terms, that's p, eg. 1.5 = 3/2 -> 3
    private static decimal GetIntegerStep(decimal step)
    {
        var denominator = Pow10(step.Scale);
        var numerator = step * denominator;
        return numerator / GreatestCommonDivisor(numerator, denominator);
    }

    private static decimal LeastCommonMultiple(decimal a, decimal b)
    {
        try
        {
            var scale = Pow10(Math.Max(a.Scale, b.Scale));
            var scaledA = a * scale;
            var scaledB = b * scale;
            return scaledA / GreatestCommonDivisor(scaledA, scaledB) * scaledB / scale;
        }
        catch (OverflowException)
        {
            return Math.Max(a, b);
        }
    }

    private static decimal GreatestCommonDivisor(decimal a, decimal b)
    {
        a = decimal.Truncate(a);
        b = decimal.Truncate(b);
        while (b != 0)
        {
            (a, b) = (b, a % b);
        }
        return a;
    }

    private static decimal Pow10(int exponent)
    {
        var result = 1m;
        for (var i = 0; i < exponent; i++)
        {
            result *= 10;
        }
        return result;
    }

    private static bool TryParseBound(string? bound, out decimal value) =>
        decimal.TryParse(bound, NumberStyles.Float, CultureInfo.InvariantCulture, out value);

    private static JsonSchemaType? GetEffectiveType(IOpenApiSchema schema)
    {
        if (schema.Type is JsonSchemaType type)
        {
            // pick the first non-null type when multiple types are allowed
            foreach (var candidate in new[]
            {
                JsonSchemaType.Object, JsonSchemaType.Array, JsonSchemaType.String,
                JsonSchemaType.Integer, JsonSchemaType.Number, JsonSchemaType.Boolean
            })
            {
                if (type.HasFlag(candidate))
                {
                    return candidate;
                }
            }

            if (type.HasFlag(JsonSchemaType.Null))
            {
                return JsonSchemaType.Null;
            }
        }

        if (schema.Properties is { Count: > 0 } || schema.AdditionalProperties is not null)
        {
            return JsonSchemaType.Object;
        }

        if (schema.Items is not null)
        {
            return JsonSchemaType.Array;
        }

        return null;
    }

    private static void MergeInto(JsonObject target, JsonObject source)
    {
        foreach (var (key, value) in source)
        {
            // merge nested objects so that properties from all sources are kept
            if (value is JsonObject sourceObject && target[key] is JsonObject targetObject)
            {
                MergeInto(targetObject, sourceObject);
                continue;
            }

            target[key] = value?.DeepClone();
        }
    }

    private static JsonObject? Describe(IOpenApiSchema? schema, int depth, HashSet<string> visiting)
    {
        if (schema is null || depth > MaxDepth)
        {
            return null;
        }

        var referenceId = GetReferenceId(schema);
        if (referenceId is not null && !visiting.Add(referenceId))
        {
            return null;
        }

        try
        {
            return DescribeCore(schema, depth, visiting);
        }
        finally
        {
            if (referenceId is not null)
            {
                _ = visiting.Remove(referenceId);
            }
        }
    }

    private static JsonObject DescribeCore(IOpenApiSchema schema, int depth, HashSet<string> visiting)
    {
        var description = new JsonObject();

        var allOfSchemas = schema.AllOf?.Where(s => s is not null).ToArray() ?? [];
        if (allOfSchemas.Length > 0)
        {
            foreach (var subSchema in allOfSchemas)
            {
                MergeDescription(description, Describe(subSchema, depth + 1, visiting));
            }
        }

        var alternative = schema.OneOf?.FirstOrDefault() ?? schema.AnyOf?.FirstOrDefault();
        if (alternative is not null)
        {
            MergeDescription(description, Describe(alternative, depth + 1, visiting));
        }

        var type = GetEffectiveType(schema);
        if (type is not null)
        {
            description["type"] = type.Value.ToString().ToLowerInvariant();
        }
        if (!string.IsNullOrEmpty(schema.Format))
        {
            description["format"] = schema.Format;
        }
        if (!string.IsNullOrEmpty(schema.Description))
        {
            description["description"] = schema.Description;
        }
        if (schema.Minimum is not null)
        {
            MergeDescription(description, new JsonObject { ["minimum"] = ToNumberNode(schema.Minimum.ToString()) });
        }
        if (schema.Maximum is not null)
        {
            MergeDescription(description, new JsonObject { ["maximum"] = ToNumberNode(schema.Maximum.ToString()) });
        }
        if (schema.Enum is { Count: > 0 })
        {
            description["enum"] = new JsonArray([.. schema.Enum.Select(e => e?.DeepClone())]);
        }
#pragma warning disable CS0618
        if (schema.Example is not null)
        {
            description["example"] = schema.Example.DeepClone();
        }
#pragma warning restore CS0618

        if (schema.Properties is { Count: > 0 })
        {
            var properties = description["properties"] as JsonObject ?? [];
            foreach (var (name, propertySchema) in schema.Properties)
            {
                if (propertySchema is null || propertySchema.WriteOnly)
                {
                    continue;
                }
                var propertyDescription = Describe(propertySchema, depth + 1, visiting);
                if (propertyDescription is not null)
                {
                    properties[name] = propertyDescription;
                }
            }
            description["properties"] = properties;
        }
        if (schema.MinLength is int minLength)
        {
            description["minLength"] = minLength;
        }
        if (schema.MaxLength is int maxLength)
        {
            description["maxLength"] = maxLength;
        }
        if (!string.IsNullOrEmpty(schema.Pattern))
        {
            description["pattern"] = schema.Pattern;
        }
        if (schema.MultipleOf is not null)
        {
            description["multipleOf"] = schema.MultipleOf.Value;
        }
        if (schema.MinItems is int minItems)
        {
            description["minItems"] = minItems;
        }
        if (schema.MaxItems is int maxItems)
        {
            description["maxItems"] = maxItems;
        }
        if (schema.UniqueItems == true)
        {
            description["uniqueItems"] = true;
        }
        if (schema.Required is { Count: > 0 })
        {
            MergeDescription(description, new JsonObject
            {
                ["required"] = new JsonArray([.. schema.Required.Select(r => JsonValue.Create(r))])
            });
        }
        if (schema.Items is not null)
        {
            description["items"] = Describe(schema.Items, depth + 1, visiting) ?? [];
        }
        if (schema.AdditionalProperties is not null)
        {
            description["additionalProperties"] = Describe(schema.AdditionalProperties, depth + 1, visiting) ?? [];
        }

        return description;
    }

    private static bool TryGetDecimal(JsonNode? node, out decimal value)
    {
        value = 0;
        return node is JsonValue jsonValue && jsonValue.TryGetValue(out value);
    }

    private static JsonValue? ToNumberNode(string? value) =>
        decimal.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) ?
            JsonValue.Create(number) :
            JsonValue.Create(value);

    private static void MergeDescription(JsonObject target, JsonObject? source)
    {
        if (source is null)
        {
            return;
        }

        foreach (var (key, value) in source)
        {
            if (key == "properties" && value is JsonObject sourceProperties &&
                target["properties"] is JsonObject targetProperties)
            {
                foreach (var (name, sourceProperty) in sourceProperties)
                {
                    if (sourceProperty is JsonObject sourcePropertyObject &&
                        targetProperties[name] is JsonObject targetPropertyObject)
                    {
                        MergeDescription(targetPropertyObject, sourcePropertyObject);
                    }
                    else
                    {
                        targetProperties[name] = sourceProperty?.DeepClone();
                    }
                }
                continue;
            }
            // allOf requires all constraints, so keep the stricter bound
            if (key is "minimum" or "maximum" &&
                TryGetDecimal(value, out var sourceBound) &&
                TryGetDecimal(target[key], out var targetBound))
            {
                var isStricter = key == "minimum" ? sourceBound > targetBound : sourceBound < targetBound;
                if (isStricter)
                {
                    target[key] = value!.DeepClone();
                }
                continue;
            }
            if (key == "required" && value is JsonArray sourceRequired &&
                target["required"] is JsonArray targetRequired)
            {
                foreach (var name in sourceRequired.Select(r => r?.GetValue<string>()).Where(n => n is not null))
                {
                    if (!targetRequired.Any(r => r?.GetValue<string>() == name))
                    {
                        targetRequired.Add(name);
                    }
                }
                continue;
            }

            target[key] = value?.DeepClone();
        }
    }
}
