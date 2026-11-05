// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.ApiControllers;
using DevProxy.Commands;
using DevProxy.Jwt;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using System.ComponentModel.DataAnnotations;
using System.Reflection;
using System.Text.Json;

using Xunit;

namespace DevProxy.Tests;

public class ApiEndpointSchemasTests
{
    public static TheoryData<string, string, string> ControllerEndpoints()
    {
        var data = new TheoryData<string, string, string>();
        var controllerRoute = "/" + nameof(ProxyController).Replace("Controller", "", StringComparison.Ordinal).ToLowerInvariant();
        foreach (var action in typeof(ProxyController).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
        {
            foreach (var attribute in action.GetCustomAttributes<HttpMethodAttribute>())
            {
                var path = string.IsNullOrEmpty(attribute.Template) ? controllerRoute : $"{controllerRoute}/{attribute.Template}";
                foreach (var method in attribute.HttpMethods)
                {
                    data.Add(method, path, action.Name);
                }
            }
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(ControllerEndpoints))]
    public void Get_EveryControllerEndpoint_HasSchema(string method, string path, string actionName)
    {
        _ = actionName;
        var schema = ApiEndpointSchemas.Get(method, path);

        Assert.NotNull(schema);
        Assert.NotEmpty(schema.Responses);
    }

    [Theory]
    [MemberData(nameof(ControllerEndpoints))]
    public void Get_QueryParameters_MatchControllerAction(string method, string path, string actionName)
    {
        var action = GetAction(actionName);
        var expected = action.GetParameters()
            .Where(p => p.GetCustomAttribute<FromQueryAttribute>() is not null)
            .ToDictionary(p => p.Name!, p => p.GetCustomAttribute<RequiredAttribute>() is not null);

        var query = ApiEndpointSchemas.Get(method, path)!.Query ?? [];
        var actual = query.ToDictionary(p => p!["name"]!.GetValue<string>(), p => p!["required"]!.GetValue<bool>());

        Assert.Equal(expected.OrderBy(kv => kv.Key), actual.OrderBy(kv => kv.Key));
    }

    [Theory]
    [MemberData(nameof(ControllerEndpoints))]
    public void Get_RequestBody_PresentOnlyForFromBodyActions(string method, string path, string actionName)
    {
        var action = GetAction(actionName);
        var hasBody = action.GetParameters().Any(p => p.GetCustomAttribute<FromBodyAttribute>() is not null);

        Assert.Equal(hasBody, ApiEndpointSchemas.Get(method, path)!.RequestBody is not null);
    }

    [Fact]
    public void Get_JwtTokenRequestBody_ListsAllJwtOptionsProperties()
    {
        var properties = GetRequestBodyProperties("POST", "/proxy/jwtToken");

        Assert.Equal(CamelCasePropertyNames<JwtOptions>(), properties);
    }

    [Fact]
    public void Get_ProxyStatusResponse_ListsAllProxyInfoProperties()
    {
        var schema = ApiEndpointSchemas.Get("GET", "/proxy")!;
        var properties = schema.Responses["200"]!["content"]![ApiEndpointSchemas.JsonContentType]!["schema"]!["properties"]!
            .AsObject().Select(p => p.Key).Order(StringComparer.Ordinal);

        Assert.Equal(CamelCasePropertyNames<ProxyInfo>(), properties);
    }

    [Fact]
    public void Get_LogsJsonResponse_ListsAllLogEntryProperties()
    {
        var logEntryType = typeof(ProxyController).GetNestedType("LogEntryDto", BindingFlags.NonPublic)!;
        var expected = logEntryType.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name).Order(StringComparer.Ordinal);

        var schema = ApiEndpointSchemas.Get("GET", "/proxy/logs")!;
        var properties = schema.Responses["200"]!["content"]![ApiEndpointSchemas.JsonContentType]!["schema"]!["items"]!["properties"]!
            .AsObject().Select(p => p.Key).Order(StringComparer.Ordinal);

        Assert.Equal(expected, properties);
    }

    [Fact]
    public void Get_UpdateProxyRequestBody_OnlyAllowsWritableProperties()
    {
        Assert.Equal(["recording"], GetRequestBodyProperties("POST", "/proxy"));
    }

    [Theory]
    [InlineData("POST", "/proxy/stopProxy", "202")]
    [InlineData("POST", "/proxy/mockRequest", "202")]
    [InlineData("GET", "/proxy/rootCertificate", "400")]
    public void Get_DocumentsNonDefaultStatusCodes(string method, string path, string statusCode)
    {
        Assert.True(ApiEndpointSchemas.Get(method, path)!.Responses.ContainsKey(statusCode));
    }

    [Fact]
    public void Get_UnknownEndpoint_ReturnsNull()
    {
        Assert.Null(ApiEndpointSchemas.Get("DELETE", "/proxy"));
    }

    private static MethodInfo GetAction(string name) =>
        typeof(ProxyController).GetMethod(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)!;

    private static IEnumerable<string> GetRequestBodyProperties(string method, string path) =>
        ApiEndpointSchemas.Get(method, path)!.RequestBody!["content"]![ApiEndpointSchemas.JsonContentType]!["schema"]!["properties"]!
            .AsObject().Select(p => p.Key).Order(StringComparer.Ordinal);

    private static IEnumerable<string> CamelCasePropertyNames<T>() =>
        typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => JsonNamingPolicy.CamelCase.ConvertName(p.Name))
            .Order(StringComparer.Ordinal);
}
