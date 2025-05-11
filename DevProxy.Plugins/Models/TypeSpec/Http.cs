// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Abstractions.Utils;
using System.Globalization;
using System.Text;

namespace DevProxy.Plugins.Models.TypeSpec;

internal enum ParameterLocation
{
    Path,
    Query,
    Header,
    Body
}

internal sealed class TypeSpecFile
{
    public required string Name { get; init; }
    public required Service Service { get; init; }

    public override string ToString()
    {
        var sb = new StringBuilder();
        _ = sb.AppendLine("import \"@typespec/http\";")
            .AppendLine("import \"@typespec/openapi\";")
            .AppendLine()
            .AppendLine("using TypeSpec.Http;")
            .AppendLine("using TypeSpec.OpenAPI;");
        if (Service is not null)
        {
            _ = sb.AppendLine()
                .Append(Service.ToString());
        }
        return sb.ToString();
    }
}

internal sealed class Service
{
    public required Namespace Namespace { get; init; }
    public List<Server> Servers { get; init; } = [];
    public required string Title { get; init; }

    public override string ToString()
    {
        var sb = new StringBuilder();
        _ = sb.AppendLine(CultureInfo.InvariantCulture, $"@extension(\"x-ms-generated-by\", #{{ toolName: \"Dev Proxy\", toolVersion: \"{ProxyUtils.ProductVersion}\" }})")
            .AppendLine(CultureInfo.InvariantCulture, $"@service(#{{ title: \"{Title}\" }})");
        foreach (var server in Servers)
        {
            _ = sb.AppendLine(server.ToString());
        }
        _ = sb.Append(Namespace.ToString());
        return sb.ToString();
    }
}

internal sealed class Server
{
    public string? Description { get; init; }
    public required string Url { get; init; }

    public override string ToString()
    {
        var sb = new StringBuilder();
        _ = sb.Append(CultureInfo.InvariantCulture, $"@server(\"{Url}\"");
        if (!string.IsNullOrEmpty(Description))
        {
            _ = sb.Append(CultureInfo.InvariantCulture, $", \"{Description}\"");
        }
        _ = sb.Append(')');
        return sb.ToString();
    }
}

internal sealed class Namespace
{
    public required string Name { get; init; }
    public List<Model> Models { get; init; } = [];
    public List<Operation> Operations { get; init; } = [];
    public OAuth2Auth? Auth { get; set; }

    public override string ToString()
    {
        var sb = new StringBuilder();

        _ = sb.Append(CultureInfo.InvariantCulture, $"namespace {Name}")
            .AppendLine(";");

        if (Auth is not null)
        {
            _ = sb.AppendLine()
                .AppendLine(Auth.WriteAlias());
        }

        foreach (var model in Models.Where(m => !m.IsArray))
        {
            _ = sb.AppendLine()
                .AppendLine(model.ToString());
        }

        var responseModels = Operations
            .SelectMany(o => o.Responses)
            .DistinctBy(r => r.GetModelName());
        foreach (var model in responseModels)
        {
            _ = sb.AppendLine()
                .AppendLine(model.ToString());
        }

        foreach (var operation in Operations)
        {
            _ = sb.AppendLine()
                .AppendLine(operation.ToString());
        }

        return sb.ToString();
    }
}

internal sealed class Model
{
    public required string Name { get; set; }
    public bool IsArray { get; set; }
    public bool IsError { get; set; }
    public List<ModelProperty> Properties { get; init; } = [];

    public override string ToString()
    {
        if (IsArray)
        {
            return string.Empty;
        }

        var sb = new StringBuilder();
        if (IsError)
        {
            _ = sb.AppendLine("@error");
        }
        _ = sb.AppendLine(CultureInfo.InvariantCulture, $"model {Name} {{");
        foreach (var property in Properties)
        {
            _ = sb.AppendLine(CultureInfo.InvariantCulture, $"  {property.ToString()}");
        }
        _ = sb.Append('}');
        return sb.ToString();
    }
}

internal sealed class ModelProperty
{
    public required string Name { get; init; }
    public required string Type { get; set; }

    public override string ToString() => $"{Name}: {Type};";
}

internal sealed class Operation
{
    public required TypeSpecFile Doc { get; init; }
    public required string Name { get; set; }
    public Auth? Auth { get; set; }
    public string? Description { get; set; }
    public HttpVerb Method { get; set; }
    public List<Parameter> Parameters { get; init; } = [];
    public List<OperationResponseModel> Responses { get; init; } = [];
    public string? Route { get; set; }

    public override string ToString()
    {
        var sb = new StringBuilder();
        if (!string.IsNullOrEmpty(Route))
        {
            _ = sb.AppendLine(CultureInfo.InvariantCulture, $"@route(\"{Route}\")");
        }
        _ = sb.AppendLine(CultureInfo.InvariantCulture, $"@{Method.ToString().ToLowerInvariant()}");
        if (!string.IsNullOrEmpty(Description))
        {
            _ = sb.AppendLine(CultureInfo.InvariantCulture, $"@doc(\"{Description}\")");
        }
        if (Auth is not null)
        {
            _ = sb.AppendLine(CultureInfo.InvariantCulture, $"@useAuth({Auth.ToString()})");
        }
        _ = sb.Append(CultureInfo.InvariantCulture, $"op {Name}(")
            .AppendJoin(", ", Parameters.Select(p => p.ToString(this)))
            .Append("): ")
            .AppendJoin(" | ", Responses.Select(r => r.GetModelName()))
            .Append(';');
        return sb.ToString();
    }
}

internal enum HttpVerb
{
    Get,
    Put,
    Post,
    Patch,
    Delete,
    Head
}

internal sealed class OperationResponseModel
{
    public required int StatusCode { get; init; }
    public string? BodyType { get; set; }
    public Dictionary<string, string> Headers { get; init; } = [];

    public override string ToString()
    {
        var sb = new StringBuilder();
        _ = sb.AppendLine(CultureInfo.InvariantCulture, $"model {GetModelName()} {{")
            .AppendLine(CultureInfo.InvariantCulture, $"  ...{GetResponseType()};");
        if (!string.IsNullOrEmpty(BodyType))
        {
            _ = sb.AppendLine(CultureInfo.InvariantCulture, $"  ...Body<{BodyType}>;");
        }
        _ = sb.Append('}');
        return sb.ToString();
    }

    private string GetResponseType()
    {
        return StatusCode switch
        {
            200 => "OkResponse",
            201 => "CreatedResponse",
            202 => "AcceptedResponse",
            204 => "NoContentResponse",
            301 => "MovedResponse",
            304 => "NotModifiedResponse",
            400 => "BadRequestResponse",
            401 => "UnauthorizedResponse",
            403 => "ForbiddenResponse",
            404 => "NotFoundResponse",
            409 => "ConflictResponse",
            _ => $"Response<{StatusCode}>",
        };
    }

    public string GetModelName()
    {
        var sb = new StringBuilder();
        _ = sb.Append(BodyType?.ToPascalCase().Replace("[]", "List", StringComparison.OrdinalIgnoreCase));
        var verb = StatusCode switch
        {
            201 => "Created",
            202 => "Accepted",
            204 => "NoContent",
            301 => "Moved",
            304 => "NotModified",
            400 => "Error",
            401 => "Unauthorized",
            403 => "Forbidden",
            404 => "NotFound",
            409 => "Conflict",
            _ => string.Empty
        };
        _ = sb.Append(verb).Append("Response");
        return sb.ToString();
    }
}

internal sealed class Parameter
{
    public required ParameterLocation In { get; init; }
    public required string Name { get; init; }
    public string? Value { get; init; }

    public override string ToString()
    {
        throw new NotImplementedException("Use ToString(Operation op) instead.");
    }

    public string ToString(Operation op)
    {
        var value = Value?.IndexOfAny([' ', '/', '-', ';']) == -1
            ? Value
            : $"\"{Value}\"";
        value = op.Method == HttpVerb.Patch && In == ParameterLocation.Body
            ? $"MergePatchUpdate<{value}>"
            : value;
        if (Name.IndexOf('-', StringComparison.OrdinalIgnoreCase) > -1)
        {
            var target = Name;
            var name = Name.ToCamelFromKebabCase();
            return $"@{In.ToString().ToLowerInvariant()}(\"{target}\") {name}: {value}";
        }
        else
        {
            return $"@{In.ToString().ToLowerInvariant()} {Name.ToCamelCase()}: {value}";
        }
    }
}