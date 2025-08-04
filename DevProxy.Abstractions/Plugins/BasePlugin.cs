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

    public abstract string Name { get; }

    public virtual Option[] GetOptions() => [];
    public virtual Command[] GetCommands() => [];

    public virtual Task InitializeAsync(InitArgs e, CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    public virtual void OptionsLoaded(OptionsLoadedArgs e)
    {
    }

    public virtual Task BeforeRequestAsync(ProxyRequestArgs e, CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    public virtual Task BeforeResponseAsync(ProxyResponseArgs e, CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    public virtual Task AfterResponseAsync(ProxyResponseArgs e, CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    public virtual Task AfterRequestLogAsync(RequestLogArgs e, CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    public virtual Task AfterRecordingStopAsync(RecordingArgs e, CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    public virtual Task MockRequestAsync(EventArgs e, CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}

public abstract class BasePlugin<TConfiguration>(
    HttpClient httpClient,
    ILogger logger,
    ISet<UrlToWatch> urlsToWatch,
    IProxyConfiguration proxyConfiguration,
    IConfigurationSection pluginConfigurationSection) :
    BasePlugin(logger, urlsToWatch), IPlugin<TConfiguration> where TConfiguration : new()
{
    private TConfiguration? _configuration;
    private readonly HttpClient _httpClient = httpClient;

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

    protected virtual IEnumerable<string>? GetConfigurationValue(string key, IEnumerable<string>? list, IEnumerable<string>? defaultList = default)
    {
        ArgumentNullException.ThrowIfNull(key, nameof(key));

        var keyExists = ConfigurationSection.GetChildren().Any(f => string.Equals(key, f.Key, StringComparison.Ordinal));
        list = list?.Where(static p => !string.IsNullOrEmpty(p)).ToArray();
        return keyExists ? list ?? [] : defaultList;
    }

    public virtual void Register(IServiceCollection services, TConfiguration configuration)
    {
    }

    public override async Task InitializeAsync(InitArgs e, CancellationToken cancellationToken)
    {
        await base.InitializeAsync(e, cancellationToken);

        var (IsValid, ValidationErrors) = await ValidatePluginConfigAsync(cancellationToken);
        if (!IsValid)
        {
            Logger.LogError("Plugin configuration validation failed with the following errors: {Errors}", string.Join(", ", ValidationErrors));
        }
    }

    private async Task<(bool IsValid, IEnumerable<string> ValidationErrors)> ValidatePluginConfigAsync(CancellationToken cancellationToken)
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
            var configFile = await File.ReadAllTextAsync(ProxyConfiguration.ConfigFile, cancellationToken);

            using var document = JsonDocument.Parse(configFile, ProxyUtils.JsonDocumentOptions);
            var root = document.RootElement;

            if (!root.TryGetProperty(configSectionName, out var configSection))
            {
                Logger.LogError("Configuration section {SectionName} not found in configuration file", configSectionName);
                return (false, [string.Format(CultureInfo.InvariantCulture, "Configuration section {0} not found in configuration file", configSectionName)]);
            }

            ProxyUtils.ValidateSchemaVersion(schemaUrl, Logger);
            return await ProxyUtils.ValidateJsonAsync(configSection.GetRawText(), schemaUrl, _httpClient, Logger, cancellationToken);
        }
        catch (Exception ex)
        {
            return (false, [ex.Message]);
        }
    }
}