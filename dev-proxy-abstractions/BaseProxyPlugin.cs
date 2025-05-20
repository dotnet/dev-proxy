// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.CommandLine;
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace DevProxy.Abstractions;

public abstract class BaseProxyPlugin : IProxyPlugin
{
    protected ISet<UrlToWatch> UrlsToWatch { get; }
    protected ILogger Logger { get; }
    protected IConfigurationSection? ConfigSection { get; }
    protected IPluginEvents PluginEvents { get; }
    protected IProxyContext Context { get; }

    public virtual string Name => throw new NotImplementedException();

    public virtual Option[] GetOptions() => [];
    public virtual Command[] GetCommands() => [];

    public BaseProxyPlugin(IPluginEvents pluginEvents,
                         IProxyContext context,
                         ILogger logger,
                         ISet<UrlToWatch> urlsToWatch,
                         IConfigurationSection? configSection = null)
    {
        ArgumentNullException.ThrowIfNull(pluginEvents);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(logger);

        if (urlsToWatch is null || !urlsToWatch.Any())
        {
            throw new ArgumentException($"{nameof(urlsToWatch)} cannot be null or empty", nameof(urlsToWatch));
        }

        UrlsToWatch = urlsToWatch;
        Context = context;
        Logger = logger;
        ConfigSection = configSection;
        PluginEvents = pluginEvents;
    }

    public virtual async Task RegisterAsync()
    {
        var (IsValid, ValidationErrors) = await ValidatePluginConfig();
        if (!IsValid)
        {
            Logger.LogError("Plugin configuration validation failed with the following errors: {Errors}", string.Join(", ", ValidationErrors));
        }
    }

    protected async Task<(bool IsValid, IEnumerable<string> ValidationErrors)> ValidatePluginConfig()
    {
        if (!Context.Configuration.ValidateSchemas || ConfigSection is null)
        {
            Logger.LogDebug("Schema validation is disabled or no configuration section specified");
            return (true, []);
        }

        try
        {
            var schemaUrl = ConfigSection.GetValue<string>("$schema");
            if (string.IsNullOrWhiteSpace(schemaUrl))
            {
                Logger.LogDebug("No schema URL found in configuration file");
                return (true, []);
            }

            var configSectionName = ConfigSection.Key;
            var configFile = await File.ReadAllTextAsync(Context.Configuration.ConfigFile);

            using var document = JsonDocument.Parse(configFile, ProxyUtils.JsonDocumentOptions);
            var root = document.RootElement;

            if (!root.TryGetProperty(configSectionName, out var configSection))
            {
                Logger.LogError("Configuration section {SectionName} not found in configuration file", configSectionName);
                return (false, [string.Format(CultureInfo.InvariantCulture, "Configuration section {0} not found in configuration file", configSectionName)]);
            }

            return await ProxyUtils.ValidateJson(configSection.GetRawText(), schemaUrl, Logger);
        }
        catch (Exception ex)
        {
            return (false, [ex.Message]);
        }
    }
}
