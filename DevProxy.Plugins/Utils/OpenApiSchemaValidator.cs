// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Microsoft.OpenApi;
using System.Globalization;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DevProxy.Plugins.Utils;

internal enum SchemaValidationDirection
{
    Request,
    Response
}

/// <param name="UndefinedProperty">Name of the property when the error is
/// about a property that isn't allowed by the schema.</param>
internal sealed record SchemaValidationError(string Path, string Message, string? UndefinedProperty = null);

/// <summary>
/// Lightweight validator of JSON values against OpenAPI schemas
/// (Microsoft.OpenApi object model). Supports: type (incl. nullable / type null),
/// required, properties, additionalProperties, items, enum, allOf, anyOf, oneOf
/// and $ref. Other keywords (format, min/max, pattern, etc.) are ignored.
/// </summary>
internal static class OpenApiSchemaValidator
{
    private const int MaxDepth = 64;
    // bounds total work, e.g. for schemas that reference the same subschema repeatedly
    private const int MaxEvaluations = 1_000_000;

    private sealed class ValidationState
    {
        public int Evaluations { get; set; }
        public bool BudgetExceeded { get; set; }
        public bool StrictNullableEnums { get; init; }
        public Dictionary<(IOpenApiSchema Schema, string Path, int Depth), List<SchemaValidationError>> Memo { get; set; } = [];
    }

    public static List<SchemaValidationError> Validate(
        JsonElement value,
        IOpenApiSchema? schema,
        SchemaValidationDirection direction,
        string path = "$",
        bool strictNullableEnums = false)
    {
        var errors = new List<SchemaValidationError>();
        var state = new ValidationState { StrictNullableEnums = strictNullableEnums };
        ValidateCore(value, schema, direction, path, errors, state, 0);
        if (state.BudgetExceeded)
        {
            errors.Add(new(path, $"Validation stopped after {MaxEvaluations} schema evaluations, so the value couldn't be fully validated"));
        }
        return errors;
    }

    public static List<SchemaValidationError> Validate(
        JsonNode? value,
        IOpenApiSchema? schema,
        SchemaValidationDirection direction,
        string path = "$",
        bool strictNullableEnums = false)
    {
        using var doc = JsonDocument.Parse(value?.ToJsonString() ?? "null");
        return Validate(doc.RootElement.Clone(), schema, direction, path, strictNullableEnums);
    }

    /// <summary>
    /// Gets the possible interpretations of raw (string) parameter values as JSON
    /// values, decoding them according to the parameter's serialization style, so that
    /// they can be validated with <see cref="Validate(JsonNode?, IOpenApiSchema?, SchemaValidationDirection, string, bool)"/>.
    /// Serialized values are ambiguous, so the parameter is valid if any of the
    /// interpretations is valid. The first interpretation is the most likely one:
    /// values coerced to the primitive type declared by the schema when possible.
    /// When the schema declares multiple items schemas (e.g. in anyOf
    /// branches), items are coerced using each of them. Other interpretations keep
    /// values as strings, treat values as scalars when the
    /// schema allows both arrays and scalars, and when <paramref name="decode"/> is set,
    /// decode array items after splitting, so that encoded delimiters are kept in items.
    /// </summary>
    /// <param name="values">Raw values; when <paramref name="decode"/> is set, they're still encoded.</param>
    /// <param name="decode">Function to decode (e.g. URI-unescape) values, or null if they aren't encoded.</param>
    public static IEnumerable<JsonNode?> GetParameterValueCandidates(
        IReadOnlyList<string> values,
        IOpenApiSchema? schema,
        string name,
        ParameterStyle style,
        bool explode,
        Func<string, string>? decode = null)
    {
        var type = GetEffectiveType(schema);
        var isArray = type is not null && type.Value.HasFlag(JsonSchemaType.Array);
        IReadOnlyList<string> decodedValues = decode is null ? values : [.. values.Select(decode)];

        if (isArray)
        {
            // e.g. anyOf: [{ type: array, items: { type: integer } }, { type: array, items: { type: boolean } }]
            var itemsSchemas = GetItemsSchemas(schema);
            var splits = new List<Func<IEnumerable<string>>> { () => SplitArrayValues(decodedValues, name, style, explode) };
            if (decode is not null)
            {
                splits.Add(() => SplitArrayValues(values, name, style, explode).Select(decode));
            }

            foreach (var split in splits)
            {
                foreach (var itemsSchema in itemsSchemas)
                {
                    yield return new JsonArray([.. split().Select(item => CoercePrimitive(item, itemsSchema))]);
                }
                yield return new JsonArray([.. split().Select(item => (JsonNode?)JsonValue.Create(item))]);
            }
        }

        // e.g. anyOf: [{ type: array }, { type: string }]
        if (!isArray || (type!.Value & ~JsonSchemaType.Array) != 0)
        {
            var value = decodedValues.Count > 0 ? decodedValues[0] : string.Empty;
            var stripped = StripStylePrefix(value, name, style);
            yield return CoercePrimitive(stripped, schema);
            yield return JsonValue.Create(stripped);
        }
    }

    private static IEnumerable<string> SplitArrayValues(IReadOnlyList<string> values, string name, ParameterStyle style, bool explode)
    {
        switch (style)
        {
            case ParameterStyle.Form:
            case ParameterStyle.SpaceDelimited:
            case ParameterStyle.PipeDelimited:
                // exploded arrays are sent as repeated parameters, e.g. ids=1&ids=2
                if (explode)
                {
                    return values;
                }
                var delimiter = style switch
                {
                    ParameterStyle.SpaceDelimited => ' ',
                    ParameterStyle.PipeDelimited => '|',
                    _ => ','
                };
                return values.SelectMany(v => v.Split(delimiter));
            case ParameterStyle.Label:
                return values.SelectMany(v => StripStylePrefix(v, name, style).Split(explode ? '.' : ','));
            case ParameterStyle.Matrix:
                if (explode)
                {
                    // ;id=1;id=2
                    var prefix = $";{name}=";
                    return values.SelectMany(v => v.Split(prefix, StringSplitOptions.RemoveEmptyEntries));
                }
                return values.SelectMany(v => StripStylePrefix(v, name, style).Split(','));
            case ParameterStyle.Simple:
            default:
                return values.SelectMany(v => v.Split(',').Select(i => i.Trim()));
        }
    }

    private static string StripStylePrefix(string value, string name, ParameterStyle style) => style switch
    {
        ParameterStyle.Label when value.StartsWith('.') => value[1..],
        ParameterStyle.Matrix when value.StartsWith($";{name}=", StringComparison.Ordinal) => value[(name.Length + 2)..],
        ParameterStyle.Matrix when value == $";{name}" => string.Empty,
        _ => value
    };

    /// <summary>
    /// Gets the type declared by the schema, or when the schema doesn't declare
    /// a type, the types declared by its allOf/anyOf/oneOf subschemas.
    /// </summary>
    public static JsonSchemaType? GetEffectiveType(IOpenApiSchema? schema)
    {
        JsonSchemaType? type = null;
        foreach (var s in EnumerateComposition(schema, s => s.Type is null))
        {
            if (s.Type is not null)
            {
                type = (type ?? 0) | s.Type.Value;
            }
        }
        return type;
    }

    // Gets the distinct items schemas declared by the schema and its allOf/anyOf/oneOf subschemas
    private static List<IOpenApiSchema?> GetItemsSchemas(IOpenApiSchema? schema)
    {
        List<IOpenApiSchema?> itemsSchemas = [.. EnumerateComposition(schema, s => s.Items is null)
            .Where(s => s.Items is not null)
            .Select(s => s.Items)
            .Distinct(ReferenceEqualityComparer.Instance)
            .Cast<IOpenApiSchema?>()];
        return itemsSchemas.Count > 0 ? itemsSchemas : [null];
    }

    /// <summary>
    /// Gets the names of the properties declared by the schema and its allOf/anyOf/oneOf subschemas.
    /// </summary>
    public static IEnumerable<string> GetPropertyNames(IOpenApiSchema? schema) =>
        EnumerateComposition(schema, _ => true)
            .SelectMany(s => s.Properties?.Keys ?? Enumerable.Empty<string>())
            .Distinct(StringComparer.Ordinal);

    /// <summary>
    /// Gets whether the schema describes a dictionary, i.e. an object whose property
    /// names aren't known in advance: it (or one of its allOf/anyOf/oneOf subschemas)
    /// declares an additionalProperties schema, or it's a free-form object that doesn't
    /// declare any properties and doesn't disallow additional properties.
    /// </summary>
    public static bool AllowsDynamicProperties(IOpenApiSchema? schema)
    {
        var schemas = EnumerateComposition(schema, _ => true).ToArray();
        return schemas.Any(s => s.AdditionalProperties is not null) ||
            (schemas.Length > 0 &&
                !schemas.Any(s => s.Properties is { Count: > 0 }) &&
                schemas.All(s => s.AdditionalPropertiesAllowed));
    }

    // Gets the primitive types of the values allowed by the enums of the schema and
    // its allOf/anyOf/oneOf subschemas, for schemas that constrain values only by enum
    private static JsonSchemaType? GetEnumType(IOpenApiSchema? schema)
    {
        JsonSchemaType? type = null;
        foreach (var value in EnumerateComposition(schema, s => s.Type is null).SelectMany(s => s.Enum ?? []))
        {
            type = (type ?? 0) | (value?.GetValueKind() switch
            {
                JsonValueKind.Number => JsonSchemaType.Integer | JsonSchemaType.Number,
                JsonValueKind.True or JsonValueKind.False => JsonSchemaType.Boolean,
                JsonValueKind.Null or null => JsonSchemaType.Null,
                _ => JsonSchemaType.String
            });
        }
        return type;
    }

    // Enumerates the schema and its allOf/anyOf/oneOf subschemas depth-first
    private static IEnumerable<IOpenApiSchema> EnumerateComposition(IOpenApiSchema? schema, Func<IOpenApiSchema, bool> descend) =>
        schema is null
            ? []
            : Traverse(schema, s => descend(s) ? GetComposedSchemas(s) : []);

    // Enumerates the schema and its descendants depth-first, yielding each
    // (resolved) schema once so that repeated references and cycles don't cause
    // exponential work. A schema first reached at the depth limit is revisited
    // when it's reached again at a shallower depth, so that its descendants
    // aren't skipped depending on the order of the branches.
    private static IEnumerable<IOpenApiSchema> Traverse(IOpenApiSchema schema, Func<IOpenApiSchema, IEnumerable<IOpenApiSchema>> getChildren)
    {
        var shallowestDepths = new Dictionary<IOpenApiSchema, int>(ReferenceEqualityComparer.Instance);
        var stack = new Stack<(IOpenApiSchema Schema, int Depth)>();
        stack.Push((schema, 0));
        while (stack.Count > 0)
        {
            var (current, depth) = stack.Pop();
            var target = ResolveTarget(current);
            if (shallowestDepths.TryGetValue(target, out var shallowestDepth))
            {
                if (depth >= shallowestDepth)
                {
                    continue;
                }
                shallowestDepths[target] = depth;
            }
            else
            {
                shallowestDepths[target] = depth;
                yield return current;
            }

            if (depth < MaxDepth)
            {
                foreach (var child in getChildren(current).Reverse())
                {
                    stack.Push((child, depth + 1));
                }
            }
        }
    }

    private static IOpenApiSchema ResolveTarget(IOpenApiSchema schema) =>
        schema is OpenApiSchemaReference reference ? (IOpenApiSchema?)reference.Target ?? schema : schema;

    // Finds the declarations of the property in the schema and its allOf
    // subschemas (incl. references), which all apply to the value
    private static IEnumerable<IOpenApiSchema> FindAllOfPropertySchemas(IOpenApiSchema schema, string propertyName)
    {
        foreach (var current in Traverse(schema, s => s.AllOf ?? []))
        {
            if (current.Properties?.TryGetValue(propertyName, out var propertySchema) == true)
            {
                yield return propertySchema;
            }
        }
    }

    private static bool IsDeclaredInSibling(IList<IOpenApiSchema> allOf, int index, string propertyName) =>
        allOf.Where((_, i) => i != index)
            .Any(s => GetPropertyNames(s).Contains(propertyName, StringComparer.Ordinal));

    private static IEnumerable<IOpenApiSchema> GetComposedSchemas(IOpenApiSchema schema) =>
        (schema.AllOf ?? []).Concat(schema.AnyOf ?? []).Concat(schema.OneOf ?? []);

    private static JsonValue? CoercePrimitive(string value, IOpenApiSchema? schema)
    {
        // schemas without a type, e.g. { enum: [1, 2] }, are interpreted using the types of their enum values
        var type = GetEffectiveType(schema) ?? GetEnumType(schema);
        if (type is null)
        {
            return JsonValue.Create(value);
        }

        if (type.Value.HasFlag(JsonSchemaType.Integer))
        {
            if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l))
            {
                return JsonValue.Create(l);
            }
            // integers without a range can exceed 64 bits, so keep them lossless
            if (BigInteger.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var big))
            {
                return JsonNode.Parse(big.ToString(CultureInfo.InvariantCulture))!.AsValue();
            }
        }
        if (type.Value.HasFlag(JsonSchemaType.Number) &&
            double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) &&
            double.IsFinite(d))
        {
            return JsonValue.Create(d);
        }
        if (type.Value.HasFlag(JsonSchemaType.Boolean) && bool.TryParse(value, out var b))
        {
            return JsonValue.Create(b);
        }
        if (type.Value.HasFlag(JsonSchemaType.Null) && value.Length == 0)
        {
            return null;
        }

        return JsonValue.Create(value);
    }

    private static void ValidateCore(
        JsonElement value,
        IOpenApiSchema? schema,
        SchemaValidationDirection direction,
        string path,
        List<SchemaValidationError> errors,
        ValidationState state,
        int depth)
    {
        if (schema is null || state.BudgetExceeded)
        {
            return;
        }
        if (++state.Evaluations > MaxEvaluations)
        {
            state.BudgetExceeded = true;
            return;
        }
        if (depth > MaxDepth)
        {
            errors.Add(new(path, $"Schema is nested deeper than the supported maximum of {MaxDepth} levels, so the value couldn't be validated"));
            return;
        }

        // errors reported for this value start here; earlier errors belong to
        // other values, so they don't need to be considered for deduplication
        var firstError = errors.Count;
        if (!ValidateType(value, schema, path, errors))
        {
            // no point validating further if the type doesn't match
            return;
        }

        ValidateEnum(value, schema, path, errors, state.StrictNullableEnums);

        if (value.ValueKind == JsonValueKind.Object)
        {
            ValidateObject(value, schema, direction, path, errors, state, depth);
        }
        else if (value.ValueKind == JsonValueKind.Array && schema.Items is not null)
        {
            var i = 0;
            foreach (var item in value.EnumerateArray())
            {
                ValidateCore(item, schema.Items, direction, $"{path}[{i}]", errors, state, depth + 1);
                i++;
            }
        }

        if (schema.AllOf is { Count: > 0 })
        {
            var seen = new HashSet<SchemaValidationError>(errors.Skip(firstError));
            for (var i = 0; i < schema.AllOf.Count; i++)
            {
                foreach (var subError in ValidateComposed(value, schema.AllOf[i], direction, path, state, depth + 1))
                {
                    var error = subError;
                    if (error.UndefinedProperty is not null &&
                        error.Path == GetPropertyPath(path, error.UndefinedProperty) &&
                        IsDeclaredInSibling(schema.AllOf, i, error.UndefinedProperty))
                    {
                        // additionalProperties: false applies to each subschema on its own,
                        // which is spec-compliant but often unexpected
                        error = error with
                        {
                            Message = $"{error.Message}. The property is declared in a sibling allOf schema; additionalProperties: false applies per subschema",
                            UndefinedProperty = null
                        };
                    }

                    // subschemas can report the same error, e.g. when they reference the same schema
                    if (seen.Add(error))
                    {
                        errors.Add(error);
                    }
                }
            }
        }

        if (schema.AnyOf is { Count: > 0 } &&
            !schema.AnyOf.Any(s => IsValid(value, s, direction, path, state, depth)))
        {
            errors.Add(new(path, "Value doesn't match any of the schemas in 'anyOf'"));
        }

        if (schema.OneOf is { Count: > 0 })
        {
            var matches = schema.OneOf.Count(s => IsValid(value, s, direction, path, state, depth));
            if (matches == 0)
            {
                errors.Add(new(path, "Value doesn't match any of the schemas in 'oneOf'"));
            }
            else if (matches > 1)
            {
                errors.Add(new(path, $"Value matches {matches} schemas in 'oneOf' but must match exactly one"));
            }
        }
    }

    private static bool IsValid(JsonElement value, IOpenApiSchema schema, SchemaValidationDirection direction, string path, ValidationState state, int depth) =>
        ValidateComposed(value, schema, direction, path, state, depth + 1).Count == 0;

    // Composition keywords can reference the same subschema many times for the
    // same value. Within a single validation, a path identifies a value, so
    // results are memoized per (resolved schema, path, depth) to avoid exponential
    // work. Depth is part of the key, because results near the depth limit differ.
    private static List<SchemaValidationError> ValidateComposed(JsonElement value, IOpenApiSchema schema, SchemaValidationDirection direction, string path, ValidationState state, int depth)
    {
        var target = ResolveTarget(schema);
        if (state.Memo.TryGetValue((target, path, depth), out var cached))
        {
            return cached;
        }

        var subErrors = new List<SchemaValidationError>();
        ValidateCore(value, schema, direction, path, subErrors, state, depth);
        state.Memo[(target, path, depth)] = subErrors;
        return subErrors;
    }

    private static bool ValidateType(JsonElement value, IOpenApiSchema schema, string path, List<SchemaValidationError> errors)
    {
        if (schema.Type is not { } type || type == 0)
        {
            return true;
        }

        var matches = value.ValueKind switch
        {
            JsonValueKind.Null => type.HasFlag(JsonSchemaType.Null),
            JsonValueKind.True or JsonValueKind.False => type.HasFlag(JsonSchemaType.Boolean),
            JsonValueKind.String => type.HasFlag(JsonSchemaType.String),
            JsonValueKind.Object => type.HasFlag(JsonSchemaType.Object),
            JsonValueKind.Array => type.HasFlag(JsonSchemaType.Array),
            JsonValueKind.Number => type.HasFlag(JsonSchemaType.Number) ||
                (type.HasFlag(JsonSchemaType.Integer) && IsInteger(value)),
            JsonValueKind.Undefined => true,
            _ => true
        };

        if (!matches)
        {
            errors.Add(new(path, $"Expected {DescribeType(type)} but got {DescribeValueKind(value)}"));
        }

        return matches;
    }

    // integers can exceed the range of long and decimal, e.g. when the schema doesn't limit them
    private static bool IsInteger(JsonElement value) =>
        value.TryGetInt64(out _) ||
        (value.TryGetDecimal(out var d) && decimal.Truncate(d) == d) ||
        BigInteger.TryParse(value.GetRawText(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out _);

    private static void ValidateEnum(JsonElement value, IOpenApiSchema schema, string path, List<SchemaValidationError> errors, bool strictNullableEnums)
    {
        if (schema.Enum is not { Count: > 0 })
        {
            return;
        }

        var node = JsonNode.Parse(value.GetRawText());
        if (schema.Enum.Any(e => JsonNode.DeepEquals(e, node)))
        {
            return;
        }

        // OAS 3.0 nullable enums: null is allowed if the schema allows null.
        // Microsoft.OpenApi maps 'nullable' to the null type, so this can't be
        // distinguished from OAS 3.1, where null must be listed in the enum
        if (!strictNullableEnums && node is null && schema.Type is { } type && type.HasFlag(JsonSchemaType.Null))
        {
            return;
        }

        var allowed = string.Join(", ", schema.Enum.Select(e => e?.ToJsonString() ?? "null"));
        errors.Add(new(path, $"Value {value.GetRawText()} isn't one of the allowed values: {allowed}"));
    }

    private static void ValidateObject(
        JsonElement value,
        IOpenApiSchema schema,
        SchemaValidationDirection direction,
        string path,
        List<SchemaValidationError> errors,
        ValidationState state,
        int depth)
    {
        if (schema.Required is { Count: > 0 })
        {
            foreach (var requiredProperty in schema.Required)
            {
                if (value.TryGetProperty(requiredProperty, out _))
                {
                    continue;
                }

                // readOnly properties aren't sent in requests, writeOnly
                // properties aren't returned in responses
                if (FindAllOfPropertySchemas(schema, requiredProperty).Any(propertySchema =>
                    (direction == SchemaValidationDirection.Request && propertySchema.ReadOnly) ||
                    (direction == SchemaValidationDirection.Response && propertySchema.WriteOnly)))
                {
                    continue;
                }

                errors.Add(new(path, $"Missing required property '{requiredProperty}'"));
            }
        }

        var propertyNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
        {
            var propertyPath = GetPropertyPath(path, property.Name);
            var propertySchema = schema.Properties?.TryGetValue(property.Name, out var declaredSchema) == true
                ? declaredSchema
                : schema.AdditionalProperties;
            if (propertySchema is null)
            {
                if (!schema.AdditionalPropertiesAllowed)
                {
                    errors.Add(new(propertyPath, $"Property '{property.Name}' isn't defined in the schema", property.Name));
                }
                continue;
            }

            if (propertyNames.Add(property.Name))
            {
                ValidateCore(property.Value, propertySchema, direction, propertyPath, errors, state, depth + 1);
                continue;
            }

            // duplicate property names share paths, which are used as memo keys,
            // so validate each repeated value without the memoized results of the others
            var memo = state.Memo;
            state.Memo = [];
            try
            {
                ValidateCore(property.Value, propertySchema, direction, propertyPath, errors, state, depth + 1);
            }
            finally
            {
                state.Memo = memo;
            }
        }
    }

    // Use bracket notation for property names that would make the path
    // ambiguous (e.g. 'a.b' vs. 'a' -> 'b'), because paths are also used as memo keys
    private static string GetPropertyPath(string path, string propertyName) =>
        propertyName.Length == 0 || propertyName.IndexOfAny(['.', '[', ']', '"']) >= 0
            ? $"{path}[{JsonSerializer.Serialize(propertyName)}]"
            : $"{path}.{propertyName}";

    private static string DescribeType(JsonSchemaType type)
    {
        var names = Enum.GetValues<JsonSchemaType>()
            .Where(t => type.HasFlag(t))
            .OrderBy(t => t == JsonSchemaType.Null)
            .Select(t => t.ToString().ToLowerInvariant());
        return string.Join(" or ", names);
    }

    private static string DescribeValueKind(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Null => "null",
        JsonValueKind.True or JsonValueKind.False => "boolean",
        JsonValueKind.String => "string",
        JsonValueKind.Object => "object",
        JsonValueKind.Array => "array",
        JsonValueKind.Number => IsInteger(value) ? "integer" : "number",
        _ => value.ValueKind.ToString().ToLowerInvariant()
    };
}
