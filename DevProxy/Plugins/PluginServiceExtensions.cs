// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Abstractions.Plugins;
using DevProxy.Abstractions.Proxy;
using DevProxy.Abstractions.Utils;
using DevProxy.Commands;
using DevProxy.Plugins;
using DevProxy.Proxy;
using System.Reflection;
using System.Text.RegularExpressions;

#pragma warning disable IDE0130
namespace Microsoft.Extensions.DependencyInjection;
#pragma warning restore IDE0130

static class PluginServiceExtensions
{
    public static IServiceCollection AddPlugins(
        this IServiceCollection services,
        IConfiguration configuration,
        DevProxyConfigOptions options)
    {
        var serviceProvider = services.BuildServiceProvider();
        var logger = serviceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("Plugins");
        using var scope = logger.BeginScope(nameof(AddPlugins));

        var pluginReferences = configuration.GetSection("plugins").Get<List<PluginReference>>();
        var globallyWatchedUrls = (options.UrlsToWatch ?? configuration.GetSection("urlsToWatch").Get<List<string>>() ?? []).Select(ConvertToRegex).ToList();

        var schemaUrl = configuration.GetValue<string>("$schema");
        if (string.IsNullOrWhiteSpace(schemaUrl))
        {
            logger.LogDebug("No schema URL found in configuration file, skipping schema version validation");
        }
        else
        {
            ProxyUtils.ValidateSchemaVersion(schemaUrl, logger);
        }

        if (options.Discover)
        {
            logger.LogWarning("Dev Proxy is running in URL discovery mode. Configured plugins and URLs to watch will be ignored.");
            if (pluginReferences is null)
            {
                pluginReferences = [];
            }
            else
            {
                pluginReferences.Clear();
            }

            pluginReferences.Add(new()
            {
                Name = "UrlDiscoveryPlugin",
                PluginPath = "~appFolder/plugins/DevProxy.Plugins.dll"
            });
            pluginReferences.Add(new()
            {
                Name = "PlainTextReporter",
                PluginPath = "~appFolder/plugins/DevProxy.Plugins.dll"
            });

            globallyWatchedUrls.Clear();
            globallyWatchedUrls.Add(ConvertToRegex("https://*/*"));
        }

        if (pluginReferences is null || !pluginReferences.Any(p => p.Enabled))
        {
            throw new InvalidOperationException("No plugins configured or enabled. Please add a plugin to the configuration file.");
        }

        var defaultUrlsToWatch = globallyWatchedUrls.ToHashSet();
        var configFileDirectory = string.Empty;

        if (configuration is IConfigurationRoot configurationRoot)
        {
            configFileDirectory = Path.GetDirectoryName(ProxyConfiguration.GetConfigFilePath(configurationRoot));
            if (string.IsNullOrEmpty(configFileDirectory))
            {
                throw new InvalidOperationException("Unable to resolve config file directory.");
            }
        }
        else
        {
            throw new InvalidOperationException("Unable to resolve config file directory.");
        }

        // key = location
        var pluginContexts = new Dictionary<string, PluginLoadContext>();

        foreach (var pluginRef in pluginReferences)
        {
            if (!pluginRef.Enabled)
            {
                continue;
            }

            // Load Handler Assembly if enabled
            var pluginLocation = Path.GetFullPath(Path.Combine(configFileDirectory, ProxyUtils.ReplacePathTokens(pluginRef.PluginPath.Replace('\\', Path.DirectorySeparatorChar))));

            if (!pluginContexts.TryGetValue(pluginLocation, out var pluginLoadContext))
            {
                pluginLoadContext = new(pluginLocation);
                pluginContexts.Add(pluginLocation, pluginLoadContext);
            }

            logger.LogDebug("Loading plugin {PluginName} from: {PluginLocation}", pluginRef.Name, pluginLocation);
            var assembly = pluginLoadContext.LoadFromAssemblyName(new(Path.GetFileNameWithoutExtension(pluginLocation)));
            var pluginUrlsList = pluginRef.UrlsToWatch?.Select(ConvertToRegex);
            ISet<UrlToWatch>? pluginUrls = null;

            if (pluginUrlsList is not null)
            {
                pluginUrls = pluginUrlsList.ToHashSet();
                globallyWatchedUrls.AddRange(pluginUrlsList);
            }

            try
            {
                RegisterPlugin(
                    pluginRef,
                    (pluginUrls != null && pluginUrls.Any()) ? pluginUrls : defaultUrlsToWatch,
                    assembly,
                    configuration,
                    services,
                    logger,
                    options.IsStdioMode);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to load plugin {PluginName}: {ErrorMessage}", pluginRef.Name, ex.Message);
            }
        }

        _ = services.AddSingleton<ISet<UrlToWatch>>(globallyWatchedUrls.ToHashSet());

        // Register IStdioPlugin instances - plugins that implement IStdioPlugin can participate
        // in both HTTP proxy and stdio proxy scenarios
        _ = services.AddSingleton<IEnumerable<IStdioPlugin>>(sp =>
            sp.GetServices<IPlugin>().OfType<IStdioPlugin>().ToList());

        return services;
    }

    private static void RegisterPlugin(
        PluginReference pluginRef,
        ISet<UrlToWatch> urlsToWatch,
        Assembly assembly,
        IConfiguration configuration,
        IServiceCollection services,
        ILogger logger,
        bool isStdioMode)
    {
        var pluginType = assembly.GetTypes()
            .FirstOrDefault(t => t.Name == pluginRef.Name && typeof(IPlugin).IsAssignableFrom(t));
        if (pluginType is null)
        {
            logger.LogError("Plugin {PluginName} not found in assembly {Assembly}.", pluginRef.Name, assembly);
            return;
        }

        // In stdio mode, plugins don't need URLs to watch because they
        // communicate through STDIO (JSON-RPC) rather than HTTP
        if (urlsToWatch.Count == 0 && !isStdioMode)
        {
            logger.LogError("Plugin {PluginName} must have at least one URL to watch. Please add a URL to watch in the configuration file or use the --urls-to-watch option.", pluginRef.Name);
            return;
        }

        var isConfigurable = IsConfigurablePlugin(pluginType, out var configType);

        if (!isConfigurable || configType is null)
        {
            _ = services.AddSingleton(typeof(IPlugin), sp =>
            {
                var plugin = (IPlugin)ActivatorUtilities.CreateInstance(sp, pluginType, urlsToWatch) ??
                    throw new InvalidOperationException($"Failed to create instance of plugin type {pluginType}");
                // we don't have a cancellation token right now, because this
                // stage is synchronous, but if the implementation changes
                // in the future then we won't need to change the interface
#pragma warning disable VSTHRD002
                plugin.InitializeAsync(new() { ServiceProvider = sp }, CancellationToken.None).Wait();
#pragma warning restore VSTHRD002
                return plugin;
            });
            logger.LogDebug("Registered plugin {PluginName}.", pluginRef.Name);
            return;
        }

        // if no section specified, use guid to create an empty section
        var configSection = configuration.GetSection(pluginRef.ConfigSection ?? Guid.NewGuid().ToString());

        _ = services.AddSingleton(typeof(IPlugin), sp =>
        {
            var plugin = (IPlugin)ActivatorUtilities.CreateInstance(sp, pluginType, configSection, urlsToWatch) ??
                throw new InvalidOperationException($"Failed to create instance of plugin type {pluginType}");
            // we don't have a cancellation token right now, because this
            // stage is synchronous, but if the implementation changes
            // in the future then we won't need to change the interface
#pragma warning disable VSTHRD002
            plugin.InitializeAsync(new() { ServiceProvider = sp }, CancellationToken.None).Wait();
#pragma warning restore VSTHRD002
            return plugin;
        });
        logger.LogDebug("Registered plugin {PluginName}.", pluginRef.Name);
    }

    /// <summary>
    /// Determines if the plugin type is configurable and extracts its config type.
    /// </summary>
    private static bool IsConfigurablePlugin(Type pluginType, out Type? configType)
    {
        configType = null;

        foreach (var interfaceType in pluginType.GetInterfaces())
        {
            if (interfaceType.IsGenericType &&
                interfaceType.GetGenericTypeDefinition() == typeof(IPlugin<>))
            {
                configType = interfaceType.GetGenericArguments()[0];
                return true;
            }
        }

        var baseType = pluginType;
        while (baseType is not null && baseType != typeof(object))
        {
            if (baseType.IsGenericType &&
                baseType.GetGenericTypeDefinition() == typeof(BasePlugin<>))
            {
                configType = baseType.GetGenericArguments()[0];
                return true;
            }

            baseType = baseType.BaseType;
        }

        return false;
    }

    private static UrlToWatch ConvertToRegex(string stringMatcher)
    {
        var exclude = false;
        if (stringMatcher.StartsWith('!'))
        {
            exclude = true;
            stringMatcher = stringMatcher[1..];
        }

        return new UrlToWatch(
            new Regex($"^{Regex.Escape(stringMatcher).Replace("\\*", ".*", StringComparison.OrdinalIgnoreCase)}$", RegexOptions.Compiled | RegexOptions.IgnoreCase),
            exclude
        );
    }
}
