// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.CommandLine;
using System.Globalization;
using System.Text.Json;
using DevProxy.Abstractions.Proxy;
using DevProxy.Abstractions.Utils;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DevProxy.Abstractions.Plugins;

public abstract class BasePlugin(
    ILogger logger,
    ISet<UrlToWatch> urlsToWatch) : IPlugin
{
    public bool Enabled { get; protected set; } = true;
    protected ILogger Logger { get; } = logger;
    protected ISet<UrlToWatch> UrlsToWatch { get; } = urlsToWatch;

#pragma warning disable CA1065
    public virtual string Name => throw new NotImplementedException();
#pragma warning restore CA1065

    public virtual Option[] GetOptions() => [];
    public virtual Command[] GetCommands() => [];

    public virtual Task InitializeAsync(InitArgs e)
    {
        return Task.CompletedTask;
    }

    public virtual void OptionsLoaded(OptionsLoadedArgs e)
    {
    }

    public virtual Task BeforeRequestAsync(ProxyRequestArgs e)
    {
        return Task.CompletedTask;
    }

    public virtual Task BeforeResponseAsync(ProxyResponseArgs e)
    {
        return Task.CompletedTask;
    }

    public virtual Task AfterResponseAsync(ProxyResponseArgs e)
    {
        return Task.CompletedTask;
    }

    public virtual Task AfterRequestLogAsync(RequestLogArgs e)
    {
        return Task.CompletedTask;
    }

    public virtual Task AfterRecordingStopAsync(RecordingArgs e)
    {
        return Task.CompletedTask;
    }

    public virtual Task MockRequestAsync(EventArgs e)
    {
        return Task.CompletedTask;
    }
}

public abstract class BasePlugin<TConfiguration>(
    ILogger logger,
    ISet<UrlToWatch> urlsToWatch,
    IProxyConfiguration proxyConfiguration,
    IConfigurationSection pluginConfigurationSection) :
    BasePlugin(logger, urlsToWatch), IPlugin<TConfiguration> where TConfiguration : new()
{
    private TConfiguration? _configuration;

    protected IProxyConfiguration ProxyConfiguration { get; } = proxyConfiguration;
    public TConfiguration Configuration
    {
        get
        {
            if (_configuration is null)
            {
                if (!ConfigurationSection.Exists())
                {
                    _configuration = new();
                }
                else
                {
                    _configuration = ConfigurationSection.Get<TConfiguration>();
                }
            }

            return _configuration!;
        }
    }
    public IConfigurationSection ConfigurationSection { get; } = pluginConfigurationSection;

    public virtual void Register(IServiceCollection services, TConfiguration configuration)
    {
    }

    public override async Task InitializeAsync(InitArgs e)
    {
        await base.InitializeAsync(e);

        // We need to begin a scope because we're in an abstract class with
        // a generic ILogger
        using var scope = Logger.BeginScope(Name);

        var (IsValid, ValidationErrors) = await ValidatePluginConfig();
        if (!IsValid)
        {
            Logger.LogError("Plugin configuration validation failed with the following errors: {Errors}", string.Join(", ", ValidationErrors));
        }
    }

    private async Task<(bool IsValid, IEnumerable<string> ValidationErrors)> ValidatePluginConfig()
    {
        if (!ProxyConfiguration.ValidateSchemas)
        {
            Logger.LogDebug("Schema validation is disabled");
            return (true, []);
        }

        try
        {
            var schemaUrl = ConfigurationSection.GetValue<string>("$schema");
            if (string.IsNullOrWhiteSpace(schemaUrl))
            {
                Logger.LogDebug("No schema URL found in configuration file");
                return (true, []);
            }

            var configSectionName = ConfigurationSection.Key;
            var configFile = await File.ReadAllTextAsync(ProxyConfiguration.ConfigFile);

            using var document = JsonDocument.Parse(configFile, ProxyUtils.JsonDocumentOptions);
            var root = document.RootElement;

            if (!root.TryGetProperty(configSectionName, out var configSection))
            {
                Logger.LogError("Configuration section {SectionName} not found in configuration file", configSectionName);
                return (false, [string.Format(CultureInfo.InvariantCulture, "Configuration section {0} not found in configuration file", configSectionName)]);
            }

            ProxyUtils.ValidateSchemaVersion(schemaUrl, Logger);
            return await ProxyUtils.ValidateJson(configSection.GetRawText(), schemaUrl, Logger);
        }
        catch (Exception ex)
        {
            return (false, [ex.Message]);
        }
    }
}