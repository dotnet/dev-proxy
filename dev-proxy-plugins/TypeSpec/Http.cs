// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Abstractions;
using System.Text;

namespace DevProxy.Plugins.TypeSpec;

internal class TypeSpecFile
{
    public required string Name { get; init; }
    public required Service Service { get; init; }

    public override string ToString()
    {
        var sb = new StringBuilder();
        sb.AppendLine("import \"@typespec/http\";");
        sb.AppendLine("import \"@typespec/openapi\";");
        sb.AppendLine();
        sb.AppendLine("using TypeSpec.Http;");
        sb.AppendLine("using TypeSpec.OpenAPI;");
        if (Service is not null)
        {
            sb.AppendLine();
            sb.Append(Service.ToString());
        }
        return sb.ToString();
    }
}

internal class Service
{
    public required Namespace Namespace { get; init; }
    public List<Server> Servers { get; init; } = [];
    public required string Title { get; init; }

    override public string ToString()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"@extension(\"x-ms-generated-by\", #{{ toolName: \"Dev Proxy\", toolVersion: \"{ProxyUtils.ProductVersion}\" }})");
        sb.AppendLine($"@service(#{{ title: \"{Title}\" }})");
        foreach (var server in Servers)
        {
            sb.AppendLine(server.ToString());
        }
        sb.Append(Namespace.ToString());
        return sb.ToString();
    }
}

internal class Server
{
    public string? Description { get; init; }
    public required string Url { get; init; }

    override public string ToString()
    {
        var sb = new StringBuilder();
        sb.Append($"@server(\"{Url}\"");
        if (!string.IsNullOrEmpty(Description))
        {
            sb.Append($", \"{Description}\"");
        }
        sb.Append(')');
        return sb.ToString();
    }
}

internal class Namespace
{
    public List<Model> Models { get; init; } = [];
    public required string Name { get; init; }
    public List<Operation> Operations { get; init; } = [];

    override public string ToString()
    {
        var sb = new StringBuilder();
        sb.Append($"namespace {Name}");
        sb.AppendLine(";");

        foreach (var model in Models.Where(m => !m.IsArray))
        {
            sb.AppendLine();
            sb.AppendLine(model.ToString());
        }

        var responseModels = Operations
            .SelectMany(o => o.Responses)
            .DistinctBy(r => r.GetModelName());
        foreach (var model in responseModels)
        {
            sb.AppendLine();
            sb.AppendLine(model.ToString());
        }

        foreach (var operation in Operations)
        {
            sb.AppendLine();
            sb.AppendLine(operation.ToString());
        }

        return sb.ToString();
    }
}

internal class Model
{
    public bool IsArray { get; set; }
    public bool IsError { get; set; }
    public required string Name { get; set; }
    public List<ModelProperty> Properties { get; init; } = [];

    override public string ToString()
    {
        if (IsArray)
        {
            return string.Empty;
        }

        var sb = new StringBuilder();
        if (IsError)
        {
            sb.AppendLine("@error");
        }
        sb.AppendLine($"model {Name} {{");
        foreach (var property in Properties)
        {
            sb.AppendLine($"  {property.ToString()}");
        }
        sb.Append('}');
        return sb.ToString();
    }
}

internal class ModelProperty
{
    public required string Name { get; init; }
    public required string Type { get; init; }

    override public string ToString()
    {
        return $"{Name}: {Type};";
    }
}

internal class Operation
{
    public string? Description { get; set; }
    public HttpVerb Method { get; set; }
    public required string Name { get; set; }
    public List<Parameter> Parameters { get; init; } = [];
    public List<OperationResponseModel> Responses { get; init; } = [];
    public string? Route { get; set; }

    override public string ToString()
    {
        var sb = new StringBuilder(); 
        if (!string.IsNullOrEmpty(Route))
        {
            sb.AppendLine($"@route(\"{Route}\")");
        }
        sb.AppendLine($"@{Method.ToString().ToLower()}");
        if (!string.IsNullOrEmpty(Description))
        {
            sb.AppendLine($"@doc(\"{Description}\")");
        }
        sb.Append($"op {Name}(");
        sb.AppendJoin(", ", Parameters.Select(p => p.ToString()));
        sb.Append("): ");
        sb.AppendJoin(" | ", Responses.Select(r => r.GetModelName()));
        sb.Append(';');
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

internal class OperationResponseModel
{
    public string? BodyType { get; set; }
    public Dictionary<string, string> Headers { get; init; } = [];
    public required int StatusCode { get; init; }

    public override string ToString()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"model {GetModelName()} {{");
        sb.AppendLine($"  ...{GetResponseType()};");
        if (!string.IsNullOrEmpty(BodyType))
        {
            sb.AppendLine($"  ...Body<{BodyType}>;");
        }
        sb.Append('}');
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
        sb.Append(BodyType?.ToPascalCase().Replace("[]", "List"));
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
        sb.Append(verb);
        sb.Append("Response");
        return sb.ToString();
    }
}

internal class Parameter
{
    public required ParameterLocation In { get; init; }
    public required string Name { get; init; }
    public string? Value { get; init; }

    public static string GetHeaderName(string name)
    {
        var words = name.Split('-');
        var headerName = string.Join("", words.Select(w => w.ToPascalCase()));
        return headerName.ToCamelCase();
    }

    override public string ToString()
    {
        var value = Value?.IndexOfAny([' ', '/', '-', ';']) == -1
            ? Value
            : $"\"{Value}\"";
        return $"@{In.ToString().ToLower()} {Name}: {value}";
    }
}

internal enum ParameterLocation
{
    Path,
    Query,
    Header,
    Body
}