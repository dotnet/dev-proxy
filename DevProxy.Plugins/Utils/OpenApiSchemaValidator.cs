// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Microsoft.OpenApi;
using System.Globalization;
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
/// (incl. discriminator) and $ref. Other keywords (format, min/max, pattern, etc.) are ignored.
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
        public Dictionary<(IOpenApiSchema Schema, string Path, int Depth), List<SchemaValidationError>> Memo { get; } = [];
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
    /// Converts raw (string) parameter values into a JSON value, decoding them
    /// according to the parameter's serialization style and coercing them
    /// to the primitive type declared by the schema when possible, so that they
    /// can be validated with <see cref="Validate(JsonNode?, IOpenApiSchema?, SchemaValidationDirection, string)"/>.
    /// When <paramref name="coerce"/> is false, values are kept as strings.
    /// </summary>
    public static JsonNode? CoerceParameterValue(
        IReadOnlyList<string> values,
        IOpenApiSchema? schema,
        string name,
        ParameterStyle style,
        bool explode,
        bool coerce = true)
    {
        var type = GetEffectiveType(schema);
        if (type is not null && type.Value.HasFlag(JsonSchemaType.Array))
        {
            var itemsSchema = GetItems(schema);
            var array = new JsonArray();
            foreach (var item in SplitArrayValues(values, name, style, explode))
            {
                array.Add(coerce ? CoercePrimitive(item, itemsSchema) : JsonValue.Create(item));
            }
            return array;
        }

        var value = values.Count > 0 ? values[0] : string.Empty;
        var stripped = StripStylePrefix(value, name, style);
        return coerce ? CoercePrimitive(stripped, schema) : JsonValue.Create(stripped);
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

    private static IOpenApiSchema? GetItems(IOpenApiSchema? schema) =>
        EnumerateComposition(schema, s => s.Items is null).FirstOrDefault(s => s.Items is not null)?.Items;

    /// <summary>
    /// Gets the names of the properties declared by the schema and its allOf/anyOf/oneOf subschemas.
    /// </summary>
    public static IEnumerable<string> GetPropertyNames(IOpenApiSchema? schema) =>
        EnumerateComposition(schema, _ => true)
            .SelectMany(s => s.Properties?.Keys ?? Enumerable.Empty<string>())
            .Distinct(StringComparer.Ordinal);

    // Enumerates the schema and its allOf/anyOf/oneOf subschemas depth-first,
    // visiting each (resolved) schema once so that repeated references and
    // cycles don't cause exponential work.
    private static IEnumerable<IOpenApiSchema> EnumerateComposition(IOpenApiSchema? schema, Func<IOpenApiSchema, bool> descend)
    {
        if (schema is null)
        {
            yield break;
        }

        var visited = new HashSet<IOpenApiSchema>(ReferenceEqualityComparer.Instance);
        var stack = new Stack<(IOpenApiSchema Schema, int Depth)>();
        stack.Push((schema, 0));
        while (stack.Count > 0)
        {
            var (current, depth) = stack.Pop();
            if (!visited.Add(ResolveTarget(current)))
            {
                continue;
            }

            yield return current;

            if (depth < MaxDepth && descend(current))
            {
                foreach (var subSchema in GetComposedSchemas(current).Reverse())
                {
                    stack.Push((subSchema, depth + 1));
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
        var visited = new HashSet<IOpenApiSchema>(ReferenceEqualityComparer.Instance);
        var stack = new Stack<(IOpenApiSchema Schema, int Depth)>();
        stack.Push((schema, 0));
        while (stack.Count > 0)
        {
            var (current, depth) = stack.Pop();
            if (!visited.Add(ResolveTarget(current)))
            {
                continue;
            }

            if (current.Properties?.TryGetValue(propertyName, out var propertySchema) == true)
            {
                yield return propertySchema;
            }

            if (depth < MaxDepth)
            {
                foreach (var subSchema in current.AllOf ?? [])
                {
                    stack.Push((subSchema, depth + 1));
                }
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
        var type = GetEffectiveType(schema);
        if (type is null)
        {
            return JsonValue.Create(value);
        }

        if (type.Value.HasFlag(JsonSchemaType.Integer) &&
            long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l))
        {
            return JsonValue.Create(l);
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

        if (schema.OneOf is { Count: > 0 } &&
            schema.Discriminator?.PropertyName is { Length: > 0 } &&
            value.ValueKind == JsonValueKind.Object)
        {
            ValidateDiscriminatedOneOf(value, schema, schema.Discriminator, direction, path, errors, firstError, state, depth);
        }
        else if (schema.OneOf is { Count: > 0 })
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

    // Validates the value only against the oneOf branch selected by the
    // discriminator, so that overlapping branches don't cause false positives
    private static void ValidateDiscriminatedOneOf(
        JsonElement value,
        IOpenApiSchema schema,
        OpenApiDiscriminator discriminator,
        SchemaValidationDirection direction,
        string path,
        List<SchemaValidationError> errors,
        int firstError,
        ValidationState state,
        int depth)
    {
        var propertyName = discriminator.PropertyName!;
        var oneOf = schema.OneOf!;
        var errorPath = path;
        IOpenApiSchema? candidate;
        string source;
        if (!value.TryGetProperty(propertyName, out var discriminatorValue))
        {
            if (discriminator.DefaultMapping is null)
            {
                errors.Add(new(path, $"Missing discriminator property '{propertyName}' required to select a schema in 'oneOf'"));
                return;
            }
            candidate = discriminator.DefaultMapping;
            source = "Discriminator default mapping";
        }
        else if (discriminatorValue.ValueKind != JsonValueKind.String)
        {
            errors.Add(new(GetPropertyPath(path, propertyName), $"Discriminator property '{propertyName}' must be a string but got {DescribeValueKind(discriminatorValue)}"));
            return;
        }
        else
        {
            var discriminatorName = discriminatorValue.GetString()!;
            errorPath = GetPropertyPath(path, propertyName);
            source = $"Discriminator value '{discriminatorName}'";
            // explicit mappings take precedence over implicit ones, which use
            // the name of the referenced component schema
            candidate = discriminator.Mapping is not null &&
                discriminator.Mapping.TryGetValue(discriminatorName, out var mapped)
                ? mapped
                : oneOf.FirstOrDefault(s => GetSchemaName(s) == discriminatorName);
            if (candidate is null && discriminator.DefaultMapping is not null)
            {
                candidate = discriminator.DefaultMapping;
                source = "Discriminator default mapping";
            }
            if (candidate is null)
            {
                var allowed = GetDiscriminatorValues(oneOf, discriminator);
                var allowedMessage = allowed.Count > 0
                    ? $". Allowed values: {string.Join(", ", allowed.Select(v => $"'{v}'"))}"
                    : string.Empty;
                errors.Add(new(errorPath, $"Discriminator value '{discriminatorName}' doesn't map to any schema in 'oneOf'{allowedMessage}"));
                return;
            }
        }

        // mappings must not select a schema outside the declared alternatives,
        // otherwise the value could bypass oneOf validation
        var selected = FindOneOfBranch(oneOf, candidate);
        if (selected is null)
        {
            var schemaName = GetSchemaName(candidate) is { } name ? $"schema '{name}'" : "a schema";
            errors.Add(new(errorPath, $"{source} maps to {schemaName}, which isn't one of the schemas in 'oneOf'"));
            return;
        }

        var seen = new HashSet<SchemaValidationError>(errors.Skip(firstError));
        foreach (var error in ValidateComposed(value, selected, direction, path, state, depth + 1))
        {
            if (seen.Add(error))
            {
                errors.Add(error);
            }
        }
    }

    private static IOpenApiSchema? FindOneOfBranch(IList<IOpenApiSchema> oneOf, IOpenApiSchema candidate)
    {
        var target = ResolveTarget(candidate);
        var name = GetSchemaName(candidate);
        return oneOf.FirstOrDefault(s =>
            ReferenceEquals(ResolveTarget(s), target) ||
            (name is not null && GetSchemaName(s) == name));
    }

    private static List<string> GetDiscriminatorValues(IList<IOpenApiSchema> oneOf, OpenApiDiscriminator discriminator) =>
        [.. (discriminator.Mapping ?? Enumerable.Empty<KeyValuePair<string, OpenApiSchemaReference>>())
            .Where(m => FindOneOfBranch(oneOf, m.Value) is not null)
            .Select(m => m.Key)
            .Concat(oneOf.Select(GetSchemaName).OfType<string>())
            .Distinct(StringComparer.Ordinal)];

    private static string? GetSchemaName(IOpenApiSchema schema) =>
        schema is OpenApiSchemaReference reference ? reference.Reference?.Id : null;

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

    private static bool IsInteger(JsonElement value) =>
        value.TryGetInt64(out _) ||
        (value.TryGetDecimal(out var d) && decimal.Truncate(d) == d);

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

        foreach (var property in value.EnumerateObject())
        {
            var propertyPath = GetPropertyPath(path, property.Name);
            if (schema.Properties is not null &&
                schema.Properties.TryGetValue(property.Name, out var propertySchema))
            {
                ValidateCore(property.Value, propertySchema, direction, propertyPath, errors, state, depth + 1);
            }
            else if (schema.AdditionalProperties is not null)
            {
                ValidateCore(property.Value, schema.AdditionalProperties, direction, propertyPath, errors, state, depth + 1);
            }
            else if (!schema.AdditionalPropertiesAllowed)
            {
                errors.Add(new(propertyPath, $"Property '{property.Name}' isn't defined in the schema", property.Name));
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
