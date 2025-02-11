// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Microsoft.OpenApi.Models;

namespace Microsoft.DevProxy.Plugins.ApiCenter;

internal class Collection<T>()
{
    public T[] Value { get; set; } = [];
}

internal class Api
{
    public ApiDeployment[]? Deployments { get; set; }
    public string? Id { get; set; }
    public string? Name { get; set; }
    public ApiProperties? Properties { get; set; }
    public ApiVersion[]? Versions { get; set; }
}

internal class ApiProperties
{
    public ApiContact[] Contacts { get; set; } = [];
    public dynamic CustomProperties { get; set; } = new object();
    public string? Description { get; set; }
    public ApiKind? Kind { get; set; }
    public ApiLifecycleStage? LifecycleStage { get; set; }
    public string? Title { get; set; }
    public string? Summary { get; set; }
}

internal class ApiContact
{
    public string? Email { get; set; }
    public string? Name { get; set; }
    public string? Url { get; set; }
}

internal class ApiDeployment
{
    public string? Name { get; set; }
    public ApiDeploymentProperties? Properties { get; set; }
}

internal class ApiDeploymentProperties
{
    public dynamic CustomProperties { get; set; } = new object();
    public string? DefinitionId { get; set; }
    public ApiDeploymentServer? Server { get; set; }
    public string? Title { get; set; }
}

internal class ApiDeploymentServer
{
    public string[] RuntimeUri { get; set; } = [];
}

internal class ApiDefinition
{
    public OpenApiDocument? Definition { get; set; }
    public string? Id { get; set; }
    public ApiDefinitionProperties? Properties { get; set; }
}

internal class ApiDefinitionProperties
{
    public ApiDefinitionPropertiesSpecification? Specification { get; set; }
    public string? Title { get; set; }
}

internal class ApiDefinitionPropertiesSpecification
{
    public string? Name { get; set; }
}

internal class ApiSpecImport
{
    public ApiSpecImportResultFormat Format { get; set; }
    public ApiSpecImportRequestSpecification? Specification { get; set; }
    public string? Value { get; set; }
}

internal class ApiSpecImportRequestSpecification
{
    public string? Name { get; set; }
    public string? Version { get; set; }
}

internal class ApiSpecExportResult
{
    public ApiSpecExportResultFormat? Format { get; set; }
    public string? Value { get; set; }
}

internal class ApiVersion
{
    public ApiDefinition[]? Definitions { get; set; }
    public string? Id { get; set; }
    public string? Name { get; set; }
    public ApiVersionProperties? Properties { get; set; }
}

internal class ApiVersionProperties
{
    public ApiLifecycleStage LifecycleStage { get; set; }
    public string? Title { get; set; }
}

internal enum ApiSpecImportResultFormat
{
    Inline,
    Link
}

internal enum ApiSpecExportResultFormat
{
    Inline,
    Link
}

internal enum ApiKind
{
    GraphQL,
    gRPC,
    REST,
    SOAP,
    Webhook,
    WebSocket
}

internal enum ApiLifecycleStage
{
    Deprecated,
    Design,
    Development,
    Preview,
    Production,
    Retired,
    Testing
}
