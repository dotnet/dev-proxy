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
    // limits the size of generated samples, counted in JSON nodes plus
    // string characters, so that specs with huge minItems/minLength values
    // don't exhaust memory
    internal const int MaxSampleSize = 100_000;

    /// <summary>
    /// Generates a sample value for the schema. Returns <c>false</c> when
    /// satisfying the schema requires a sample larger than <see cref="MaxSampleSize"/>.
    /// </summary>
    public static bool TryGenerateSample(IOpenApiSchema? schema, out JsonNode? sample)
    {
        var context = new GenerationContext();
        sample = GenerateSample(schema, 0, context);
        if (context.IsExceeded)
        {
            sample = null;
            return false;
        }
        return true;
    }

    /// <summary>
    /// Returns a simplified JSON Schema-like representation of the schema with
    /// all references resolved, suitable to pass to a language model.
    /// </summary>
    public static JsonObject Describe(IOpenApiSchema? schema) => Describe(schema, 0, []) ?? [];

    public static bool IsStringSchema(IOpenApiSchema? schema) =>
        schema is not null && Describe(schema)["type"]?.GetValue<string>() == "string";

    private static string? GetReferenceId(IOpenApiSchema schema) =>
        schema is OpenApiSchemaReference reference ? reference.Reference?.ReferenceV3 : null;

    private static JsonNode? GenerateSample(IOpenApiSchema? schema, int depth, GenerationContext context)
    {
        if (schema is null || depth > MaxDepth || context.IsExceeded)
        {
            return null;
        }

        // stop at recursive references to avoid deeply nested samples
        var referenceId = GetReferenceId(schema);
        if (referenceId is not null && !context.Visiting.Add(referenceId))
        {
            return null;
        }

        try
        {
            return GenerateSampleCore(schema, depth, context);
        }
        finally
        {
            if (referenceId is not null)
            {
                _ = context.Visiting.Remove(referenceId);
            }
        }
    }

    private static JsonNode? GenerateSampleCore(IOpenApiSchema schema, int depth, GenerationContext context)
    {
        // Example is obsolete in favor of Examples, but OpenAPI 3.0 specs still use it
#pragma warning disable CS0618
        if (schema.Example is not null)
        {
            return context.Clone(schema.Example);
        }
#pragma warning restore CS0618

        var example = schema.Examples?.FirstOrDefault(e => e is not null);
        if (example is not null)
        {
            return context.Clone(example);
        }

        if (schema.Default is not null)
        {
            return context.Clone(schema.Default);
        }

        var enumValue = schema.Enum?.FirstOrDefault(e => e is not null);
        if (enumValue is not null)
        {
            return context.Clone(enumValue);
        }

        if (schema.AllOf is { Count: > 0 } || GetSelectedAlternative(schema) is not null)
        {
            return GenerateCompositionSample(schema, depth, context);
        }

        var type = GetEffectiveType(schema);
        if (type is JsonSchemaType.Object)
        {
            return GenerateObjectSample(schema, depth, context);
        }
        if (type is JsonSchemaType.Array)
        {
            return GenerateArraySample(schema, depth, context);
        }
        if (type is JsonSchemaType.Null || !context.TryConsume(1))
        {
            return null;
        }

        return type switch
        {
            JsonSchemaType.Integer => JsonValue.Create(GetNumber(schema, isInteger: true)),
            JsonSchemaType.Number => JsonValue.Create(GetNumber(schema, isInteger: false)),
            JsonSchemaType.Boolean => JsonValue.Create(true),
            _ => GetStringSample(schema, context) is string text ? JsonValue.Create(text) : null
        };
    }

    private static IOpenApiSchema? GetSelectedAlternative(IOpenApiSchema schema) =>
        schema.OneOf?.FirstOrDefault() ?? schema.AnyOf?.FirstOrDefault();

    // generates a sample for a schema with allOf and/or oneOf/anyOf, using
    // the first alternative of oneOf/anyOf
    private static JsonNode? GenerateCompositionSample(IOpenApiSchema schema, int depth, GenerationContext context)
    {
        var flattened = FlattenAllOf(schema, depth).ToList();

        // for non-object values, combine the constraints of the schema, its
        // allOf branches and the selected alternative so that the value
        // satisfies all of them
        if (!flattened.Any(s => GetEffectiveType(s) == JsonSchemaType.Object))
        {
            return GenerateSample(CombineConstraints(flattened), depth + 1, context);
        }

        var merged = new JsonObject();
        if (!context.TryConsume(1))
        {
            return merged;
        }

        var branches = (schema.AllOf ?? []).Append(GetSelectedAlternative(schema));
        foreach (var branch in branches)
        {
            if (GenerateSample(branch, depth + 1, context) is JsonObject obj)
            {
                MergeInto(merged, obj);
            }
        }

        if (schema.Properties is { Count: > 0 })
        {
            MergeInto(merged, GenerateObjectSample(schema, depth, context));
        }

        // properties declared in several branches must satisfy all their schemas
        foreach (var (name, propertySchemas) in GetOverlappingProperties(flattened))
        {
            merged[name] = GenerateSample(new OpenApiSchema { AllOf = propertySchemas }, depth + 1, context);
        }
        return merged;
    }

    private static IEnumerable<(string Name, List<IOpenApiSchema> Schemas)> GetOverlappingProperties(IEnumerable<IOpenApiSchema> candidates)
    {
        var propertySchemas = new Dictionary<string, List<IOpenApiSchema>>();
        foreach (var candidate in candidates)
        {
            foreach (var (name, propertySchema) in candidate.Properties ?? new Dictionary<string, IOpenApiSchema>())
            {
                if (propertySchema is null || propertySchema.WriteOnly)
                {
                    continue;
                }

                if (!propertySchemas.TryGetValue(name, out var schemas))
                {
                    schemas = [];
                    propertySchemas[name] = schemas;
                }
                schemas.Add(propertySchema);
            }
        }

        return propertySchemas
            .Where(p => p.Value.Count > 1)
            .Select(p => (p.Key, p.Value));
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

        // generation uses the first oneOf/anyOf alternative, so its constraints apply too
        var alternative = GetSelectedAlternative(schema);
        if (alternative is not null)
        {
            foreach (var nested in FlattenAllOf(alternative, depth + 1))
            {
                yield return nested;
            }
        }
    }

    private static OpenApiSchema CombineConstraints(IEnumerable<IOpenApiSchema?> schemas)
    {
        var combined = new OpenApiSchema();
        Bound? lower = null, upper = null;
        List<JsonNode>? enumValues = null;
        List<IOpenApiSchema>? itemSchemas = null;

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
            if (schema.Enum is { Count: > 0 })
            {
                // a value must be in the enums of all branches
                enumValues = enumValues is null ?
                    [.. schema.Enum.Where(e => e is not null)] :
                    [.. enumValues.Where(v => schema.Enum.Any(e => JsonNode.DeepEquals(v, e)))];
            }
            if (schema.Type is JsonSchemaType schemaType)
            {
                combined.Type = combined.Type is JsonSchemaType current ? IntersectTypes(current, schemaType) : schemaType;
            }
            combined.Format ??= schema.Format;
            if (schema.Items is not null)
            {
                (itemSchemas ??= []).Add(schema.Items);
            }

            var (schemaLower, schemaUpper) = GetBounds(schema);
            lower = GetStricterBound(lower, schemaLower, isLower: true);
            upper = GetStricterBound(upper, schemaUpper, isLower: false);
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

        if (enumValues is not null)
        {
            combined.Enum = enumValues;
        }
        // items must satisfy the item schemas of all branches
        combined.Items = itemSchemas switch
        {
            null => null,
            [var single] => single,
            _ => new OpenApiSchema { AllOf = itemSchemas }
        };
        if (lower is Bound l)
        {
            var value = l.Value.ToString(CultureInfo.InvariantCulture);
            if (l.IsExclusive)
            {
                combined.ExclusiveMinimum = value;
            }
            else
            {
                combined.Minimum = value;
            }
        }
        if (upper is Bound u)
        {
            var value = u.Value.ToString(CultureInfo.InvariantCulture);
            if (u.IsExclusive)
            {
                combined.ExclusiveMaximum = value;
            }
            else
            {
                combined.Maximum = value;
            }
        }
        return combined;
    }

    private static JsonSchemaType IntersectTypes(JsonSchemaType a, JsonSchemaType b)
    {
        // integer is a subset of number
        static JsonSchemaType Expand(JsonSchemaType type) =>
            type.HasFlag(JsonSchemaType.Number) ? type | JsonSchemaType.Integer : type;

        var intersection = Expand(a) & Expand(b);
        if (!a.HasFlag(JsonSchemaType.Number) || !b.HasFlag(JsonSchemaType.Number))
        {
            intersection &= ~JsonSchemaType.Number;
        }
        // keep the first type for contradictory schemas
        return intersection == 0 ? a : intersection;
    }

    private static JsonObject GenerateObjectSample(IOpenApiSchema schema, int depth, GenerationContext context)
    {
        var obj = new JsonObject();
        if (!context.TryConsume(1))
        {
            return obj;
        }
        foreach (var (name, propertySchema) in schema.Properties ?? new Dictionary<string, IOpenApiSchema>())
        {
            if (propertySchema is null || propertySchema.WriteOnly)
            {
                continue;
            }

            obj[name] = GenerateSample(propertySchema, depth + 1, context);
        }

        if (obj.Count == 0 && schema.AdditionalProperties is not null)
        {
            obj["additionalProp1"] = GenerateSample(schema.AdditionalProperties, depth + 1, context);
        }

        return obj;
    }

    private static JsonArray GenerateArraySample(IOpenApiSchema schema, int depth, GenerationContext context)
    {
        if (!context.TryConsume(1))
        {
            return [];
        }

        var item = GenerateSample(schema.Items, depth + 1, context);
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

        // the item is already accounted for. Reserve the remaining items
        // before allocating them
        if (count > 1 && !context.TryConsume((long)(count - 1) * GetSize(item, context.Remaining + 1)))
        {
            return [];
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
        var seen = new HashSet<string> { GetCanonicalJson(item) };
        for (var attempt = 1; array.Count < count && attempt < count * 4; attempt++)
        {
            var variant = CreateVariant(item, schema.Items, attempt, depth);
            if (variant is not null && seen.Add(GetCanonicalJson(variant)))
            {
                array.Add(variant);
            }
        }
        return array;
    }

    // JSON with sorted object properties, so that structurally equal values,
    // like for JsonNode.DeepEquals, have the same representation
    private static string GetCanonicalJson(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                return "{" + string.Join(",", obj
                    .OrderBy(p => p.Key, StringComparer.Ordinal)
                    .Select(p => JsonValue.Create(p.Key).ToJsonString() + ":" + GetCanonicalJson(p.Value))) + "}";
            case JsonArray array:
                return "[" + string.Join(",", array.Select(GetCanonicalJson)) + "]";
            case JsonValue value when value.GetValueKind() == JsonValueKind.Number && TryParseBound(value.ToJsonString(), out var number):
                // 1 and 1.0 are equal numbers
                return (number / 1.0000000000000000000000000000m).ToString(CultureInfo.InvariantCulture);
            default:
                return node?.ToJsonString() ?? "null";
        }
    }

    private static JsonNode? CreateVariant(JsonNode? sample, IOpenApiSchema? schema, int index, int depth)
    {
        if (schema is null || sample is null || depth > MaxDepth)
        {
            return null;
        }

        var constraints = schema.AllOf is { Count: > 0 } || GetSelectedAlternative(schema) is not null ?
            CombineConstraints(FlattenAllOf(schema, depth)) :
            schema;
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

            var (lower, upper) = GetBounds(schema);

            var up = number + (index * step);
            if (upper is not Bound u || up < u.Value || (up == u.Value && !u.IsExclusive))
            {
                return JsonValue.Create(up);
            }
            var down = number - (index * step);
            return lower is not Bound l || down > l.Value || (down == l.Value && !l.IsExclusive) ? JsonValue.Create(down) : null;
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

        return null;
    }

    private static string? GetStringSample(IOpenApiSchema schema, GenerationContext context)
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
            var targetLength = schema.MaxLength is int max ? Math.Min(minLength, max) : minLength;
            if (!context.TryConsume(targetLength))
            {
                return null;
            }
            value = value.PadRight(targetLength, 'x');
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
        var (lower, upper) = GetBounds(schema);

        // use 0 unless it's outside the allowed range. 0 is a multiple of any number
        if (lower is Bound l && (l.Value > 0 || (l.Value == 0 && l.IsExclusive)))
        {
            return GetLowestValue(l, upper, multipleOf, isInteger);
        }
        if (upper is Bound u && (u.Value < 0 || (u.Value == 0 && u.IsExclusive)))
        {
            return -GetLowestValue(u.Negate(), lower?.Negate(), multipleOf, isInteger);
        }

        return 0;
    }

    // the lowest value above the lower bound
    private static decimal GetLowestValue(Bound lower, Bound? upper, decimal? multipleOf, bool isInteger)
    {
        try
        {
            if (multipleOf is decimal step)
            {
                var value = RoundUpToMultiple(lower.Value, step, isInteger);
                if (lower.IsExclusive && value <= lower.Value)
                {
                    value += isInteger ? GetIntegerStep(step) : step;
                }
                return value;
            }
            if (isInteger)
            {
                return lower.IsExclusive ? decimal.Floor(lower.Value) + 1 : decimal.Ceiling(lower.Value);
            }
            if (!lower.IsExclusive)
            {
                return lower.Value;
            }
            // stay below the upper bound when the range is narrow
            return upper is Bound u && u.Value - lower.Value <= 1 ?
                (lower.Value + u.Value) / 2 :
                lower.Value + 1;
        }
        catch (OverflowException)
        {
            return lower.Value;
        }
    }

    private static (Bound? Lower, Bound? Upper) GetBounds(IOpenApiSchema schema)
    {
        Bound? lower = null, upper = null;
        if (TryParseBound(schema.Minimum?.ToString(), out var minimum))
        {
            lower = new(minimum, false);
        }
        if (TryParseBound(schema.ExclusiveMinimum?.ToString(), out var exclusiveMinimum))
        {
            lower = GetStricterBound(lower, new(exclusiveMinimum, true), isLower: true);
        }
        if (TryParseBound(schema.Maximum?.ToString(), out var maximum))
        {
            upper = new(maximum, false);
        }
        if (TryParseBound(schema.ExclusiveMaximum?.ToString(), out var exclusiveMaximum))
        {
            upper = GetStricterBound(upper, new(exclusiveMaximum, true), isLower: false);
        }
        return (lower, upper);
    }

    private static Bound? GetStricterBound(Bound? current, Bound? candidate, bool isLower)
    {
        if (current is not Bound c)
        {
            return candidate;
        }
        if (candidate is not Bound n)
        {
            return current;
        }
        if (n.Value == c.Value)
        {
            return n.IsExclusive ? n : c;
        }
        return (isLower ? n.Value > c.Value : n.Value < c.Value) ? n : c;
    }

    private readonly record struct Bound(decimal Value, bool IsExclusive)
    {
        public Bound Negate() => new(-Value, IsExclusive);
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

        var alternative = GetSelectedAlternative(schema);
        if (alternative is not null)
        {
            MergeDescription(description, Describe(alternative, depth + 1, visiting));
        }

        var type = GetEffectiveType(schema);
        if (type is not null)
        {
            MergeDescription(description, new JsonObject { ["type"] = type.Value.ToString().ToLowerInvariant() });
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
        if (schema.ExclusiveMinimum is not null)
        {
            MergeDescription(description, new JsonObject { ["exclusiveMinimum"] = ToNumberNode(schema.ExclusiveMinimum.ToString()) });
        }
        if (schema.ExclusiveMaximum is not null)
        {
            MergeDescription(description, new JsonObject { ["exclusiveMaximum"] = ToNumberNode(schema.ExclusiveMaximum.ToString()) });
        }
        if (schema.Enum is { Count: > 0 })
        {
            MergeDescription(description, new JsonObject { ["enum"] = new JsonArray([.. schema.Enum.Select(e => e?.DeepClone())]) });
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
                if (propertyDescription is null)
                {
                    continue;
                }
                // keep the constraints collected from allOf for the same property
                if (properties[name] is JsonObject existing)
                {
                    MergeDescription(existing, propertyDescription);
                }
                else
                {
                    properties[name] = propertyDescription;
                }
            }
            description["properties"] = properties;
        }
        if (schema.MinLength is int minLength)
        {
            MergeDescription(description, new JsonObject { ["minLength"] = minLength });
        }
        if (schema.MaxLength is int maxLength)
        {
            MergeDescription(description, new JsonObject { ["maxLength"] = maxLength });
        }
        if (!string.IsNullOrEmpty(schema.Pattern))
        {
            description["pattern"] = schema.Pattern;
        }
        if (schema.MultipleOf is not null)
        {
            MergeDescription(description, new JsonObject { ["multipleOf"] = schema.MultipleOf.Value });
        }
        if (schema.MinItems is int minItems)
        {
            MergeDescription(description, new JsonObject { ["minItems"] = minItems });
        }
        if (schema.MaxItems is int maxItems)
        {
            MergeDescription(description, new JsonObject { ["maxItems"] = maxItems });
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
            MergeDescription(description, new JsonObject { ["items"] = Describe(schema.Items, depth + 1, visiting) ?? [] });
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
        return node is JsonValue jsonValue && jsonValue.GetValueKind() == JsonValueKind.Number &&
            TryParseBound(jsonValue.ToJsonString(), out value);
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
            // integer is a subset of number
            if (key == "type" && value is JsonValue sourceType && target["type"] is JsonValue targetType &&
                sourceType.TryGetValue<string>(out var sourceTypeName) && targetType.TryGetValue<string>(out var targetTypeName) &&
                sourceTypeName is "number" or "integer" && targetTypeName is "number" or "integer")
            {
                if (sourceTypeName == "integer")
                {
                    target["type"] = "integer";
                }
                continue;
            }
            if (key == "items" && value is JsonObject sourceItems && target["items"] is JsonObject targetItems)
            {
                MergeDescription(targetItems, sourceItems);
                continue;
            }
            if (key == "enum" && value is JsonArray sourceEnum && target["enum"] is JsonArray targetEnum)
            {
                // a value must be in all enums
                target["enum"] = new JsonArray([.. targetEnum
                    .Where(t => sourceEnum.Any(s => JsonNode.DeepEquals(t, s)))
                    .Select(t => t?.DeepClone())]);
                continue;
            }
            // allOf requires all constraints, so keep the stricter bound
            if (key is "minimum" or "maximum" or "exclusiveMinimum" or "exclusiveMaximum" or "minLength" or "maxLength" or "minItems" or "maxItems" &&
                TryGetDecimal(value, out var sourceBound) &&
                TryGetDecimal(target[key], out var targetBound))
            {
                var isStricter = key is "minimum" or "exclusiveMinimum" or "minLength" or "minItems" ? sourceBound > targetBound : sourceBound < targetBound;
                if (isStricter)
                {
                    target[key] = value!.DeepClone();
                }
                continue;
            }
            if (key == "multipleOf" &&
                TryGetDecimal(value, out var sourceMultipleOf) && sourceMultipleOf > 0 &&
                TryGetDecimal(target[key], out var targetMultipleOf) && targetMultipleOf > 0)
            {
                target[key] = LeastCommonMultiple(targetMultipleOf, sourceMultipleOf);
                continue;
            }
            if (key == "uniqueItems" && target[key] is JsonValue targetUnique &&
                targetUnique.TryGetValue<bool>(out var isUnique) && isUnique)
            {
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

    // returns the size of the node, counted in JSON nodes plus string
    // characters, or a value larger than limit when it exceeds it
    private static long GetSize(JsonNode? node, long limit)
    {
        long size = 0;
        var pending = new Stack<JsonNode?>();
        pending.Push(node);
        while (pending.Count > 0 && size <= limit)
        {
            var current = pending.Pop();
            size++;
            switch (current)
            {
                case JsonObject obj:
                    foreach (var (_, child) in obj)
                    {
                        pending.Push(child);
                    }
                    break;
                case JsonArray array:
                    foreach (var child in array)
                    {
                        pending.Push(child);
                    }
                    break;
                case JsonValue value when value.GetValueKind() == JsonValueKind.String && value.TryGetValue<string>(out var text):
                    size += text.Length;
                    break;
                default:
                    break;
            }
        }
        return size;
    }

    private sealed class GenerationContext
    {
        public HashSet<string> Visiting { get; } = [];
        public long Remaining { get; private set; } = MaxSampleSize;
        public bool IsExceeded { get; private set; }

        public bool TryConsume(long size)
        {
            if (IsExceeded || size > Remaining)
            {
                IsExceeded = true;
                return false;
            }
            Remaining -= size;
            return true;
        }

        public JsonNode? Clone(JsonNode node) =>
            TryConsume(GetSize(node, Remaining + 1)) ? node.DeepClone() : null;
    }
}
