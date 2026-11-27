// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Abstractions.Utils;
using DevProxy.Commands;
using DevProxy.Proxy;
using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DevProxy;

/// <summary>
/// Builds the web application for one run of the proxy. Program.cs builds a new one
/// for each hot reload, so tests can use this to exercise the same build path.
/// </summary>
static class ProxyHost
{
    /// <summary>
    /// Formats a log entry matching the JSONL envelope produced by JsonConsoleFormatter:
    /// {"type":"log","level":"...","message":"...","timestamp":"..."}
    /// </summary>
    internal static string FormatJsonLogEntry(string level, string message)
    {
#pragma warning disable CA1869 // Called at most once per process lifetime
        var jsonlOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };
#pragma warning restore CA1869
        var entry = new
        {
            type = "log",
            level,
            message,
            timestamp = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture)
        };
        return JsonSerializer.Serialize(entry, jsonlOptions);
    }

    internal static bool IsJsonOutputRequested(string[] args)
    {
        for (var i = 0; i < args.Length; i++)
        {
            if (string.Equals(args[i], DevProxyCommand.OutputOptionName, StringComparison.OrdinalIgnoreCase) &&
                i + 1 < args.Length)
            {
                return string.Equals(args[i + 1], "json", StringComparison.OrdinalIgnoreCase);
            }
        }
        return false;
    }

    internal static void WriteUnresolvedEnvironmentVariables(IConfiguration configuration, DevProxyConfigOptions options)
    {
        var isJson = options.Output == DevProxy.Abstractions.Proxy.OutputFormat.Json || IsJsonOutputRequested(Environment.GetCommandLineArgs());
        foreach (var provider in ((IConfigurationRoot)configuration).Providers.OfType<IEnvironmentVariablePlaceholderProvider>())
        {
            foreach (var name in provider.UnresolvedEnvironmentVariables)
            {
                var message = $"Environment variable {name} referenced in {provider.Path ?? "configuration file"} isn't set. Leaving the placeholder as-is";
                Console.Error.WriteLine(isJson ? FormatJsonLogEntry("warning", message) : message);
            }
        }
    }

    internal static WebApplication BuildApplication(
        DevProxyConfigOptions options, ProcessEnvironment environment, HttpProxyEnvironment proxyEnvironment)
    {
        // Don't pass command-line args to WebApplication.CreateBuilder because:
        // 1. Dev Proxy uses System.CommandLine for CLI parsing, not ASP.NET Core's CommandLineConfigurationProvider
        // 2. ConfigureDevProxyConfig clears all configuration sources anyway and only uses JSON config files
        var builder = WebApplication.CreateBuilder();

        _ = builder.Configuration.ConfigureDevProxyConfig(options);
        try
        {
            return BuildApplicationCore(builder, options, environment, proxyEnvironment);
        }
        catch
        {
            // config values with unresolved placeholders can fail startup before
            // they're logged, so report them to help troubleshoot
            WriteUnresolvedEnvironmentVariables(builder.Configuration, options);
            throw;
        }
    }

    internal static WebApplication BuildApplicationCore(
        WebApplicationBuilder builder, DevProxyConfigOptions options, ProcessEnvironment environment, HttpProxyEnvironment proxyEnvironment)
    {
        environment.Apply(builder.Configuration, options.Env);
        var changedProxyVariables = proxyEnvironment.GetChangedVariables();
        _ = Announcement.ShowOnceAsync();
        _ = builder.Logging.ConfigureDevProxyLogging(builder.Configuration, options);
        var allowedOrigins = ApiSecurity.GetAllowedOrigins(builder.Configuration);
        _ = builder.Services.ConfigureDevProxyServices(builder.Configuration, options, allowedOrigins);

        var defaultApiPort = 8897;
        var apiPort = options.ApiPort ??
            builder.Configuration.GetValue("apiPort", defaultApiPort);
        var apiAddressText = options.ApiIpAddress ?? builder.Configuration.GetValue("apiIpAddress", "127.0.0.1");
        if (!IPAddress.TryParse(apiAddressText, out var apiAddress))
        {
            throw new InvalidOperationException("apiIpAddress must be an IP address, for example 127.0.0.1 or ::1.");
        }
        _ = builder.WebHost.ConfigureKestrel(options =>
        {
            options.Listen(apiAddress, apiPort);
        });

        var app = builder.Build();

        foreach (var provider in ((IConfigurationRoot)builder.Configuration).Providers.OfType<IEnvironmentVariablePlaceholderProvider>())
        {
            EnvironmentVariablePlaceholders.LogUnresolvedVariables(
                app.Logger, provider.UnresolvedEnvironmentVariables, provider.Path ?? "configuration file");
        }

        if (changedProxyVariables.Length > 0)
        {
            app.Logger.LogWarning(
                "Proxy-routing environment variables changed ({EnvironmentVariables}). HTTP proxy settings are cached and cannot be refreshed by hot reload. Stop Dev Proxy and start it again to apply these changes.",
                string.Join(", ", changedProxyVariables));
        }

        _ = app.Use((context, next) => ApiSecurity.CheckOriginAsync(context, next, allowedOrigins));
        _ = app.UseCors();
        _ = app.Use(ApiSecurity.AuthenticateAsync);
        _ = app.MapControllers();

        return app;
    }
}
