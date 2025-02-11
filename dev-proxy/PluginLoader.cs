// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Microsoft.DevProxy.Abstractions;
using System.Reflection;
using System.Text.RegularExpressions;

namespace Microsoft.DevProxy;

internal class PluginLoaderResult(ISet<UrlToWatch> urlsToWatch, IEnumerable<IProxyPlugin> proxyPlugins)
{
    public ISet<UrlToWatch> UrlsToWatch { get; } = urlsToWatch ?? throw new ArgumentNullException(nameof(urlsToWatch));
    public IEnumerable<IProxyPlugin> ProxyPlugins { get; } = proxyPlugins ?? throw new ArgumentNullException(nameof(proxyPlugins));
}

internal class PluginLoader(ILogger logger, ILoggerFactory loggerFactory)
{
    private PluginConfig? _pluginConfig;
    private readonly ILogger _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly ILoggerFactory _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));

    public async Task<PluginLoaderResult> LoadPluginsAsync(IPluginEvents pluginEvents, IProxyContext proxyContext)
    {
        List<IProxyPlugin> plugins = [];
        var config = PluginConfig;
        var globallyWatchedUrls = PluginConfig.UrlsToWatch.Select(ConvertToRegex).ToList();
        var defaultUrlsToWatch = globallyWatchedUrls.ToHashSet();
        var configFileDirectory = Path.GetDirectoryName(Path.GetFullPath(ProxyUtils.ReplacePathTokens(ProxyHost.ConfigFile)));
        // key = location
        var pluginContexts = new Dictionary<string, PluginLoadContext>();

        if (!string.IsNullOrEmpty(configFileDirectory))
        {
            foreach (PluginReference h in config.Plugins)
            {
                if (!h.Enabled) continue;
                // Load Handler Assembly if enabled
                var pluginLocation = Path.GetFullPath(Path.Combine(configFileDirectory, ProxyUtils.ReplacePathTokens(h.PluginPath.Replace('\\', Path.DirectorySeparatorChar))));

                if (!pluginContexts.TryGetValue(pluginLocation, out PluginLoadContext? pluginLoadContext))
                {
                    pluginLoadContext = new PluginLoadContext(pluginLocation);
                    pluginContexts.Add(pluginLocation, pluginLoadContext);
                }

                _logger?.LogDebug("Loading plugin {pluginName} from: {pluginLocation}", h.Name, pluginLocation);
                var assembly = pluginLoadContext.LoadFromAssemblyName(new AssemblyName(Path.GetFileNameWithoutExtension(pluginLocation)));
                var pluginUrlsList = h.UrlsToWatch?.Select(ConvertToRegex);
                ISet<UrlToWatch>? pluginUrls = null;

                if (pluginUrlsList is not null)
                {
                    pluginUrls = pluginUrlsList.ToHashSet();
                    globallyWatchedUrls.AddRange(pluginUrlsList);
                }

                var plugin = CreatePlugin(
                    assembly,
                    h,
                    pluginEvents,
                    proxyContext,
                    (pluginUrls != null && pluginUrls.Any()) ? pluginUrls : defaultUrlsToWatch,
                    h.ConfigSection is null ? null : Configuration.GetSection(h.ConfigSection)
                );
                _logger?.LogDebug("Registering plugin {pluginName}...", plugin.Name);
                await plugin.RegisterAsync();
                _logger?.LogDebug("Plugin {pluginName} registered.", plugin.Name);
                plugins.Add(plugin);
            }
        }

        return plugins.Count > 0
            ? new PluginLoaderResult(globallyWatchedUrls.ToHashSet(), plugins)
            : throw new InvalidDataException("No plugins were loaded");
    }

    private IProxyPlugin CreatePlugin(
        Assembly assembly,
        PluginReference pluginReference,
        IPluginEvents pluginEvents,
        IProxyContext context,
        ISet<UrlToWatch> urlsToWatch,
        IConfigurationSection? configSection = null
    )
    {
        foreach (Type type in assembly.GetTypes())
        {
            if (type.Name == pluginReference.Name &&
                typeof(IProxyPlugin).IsAssignableFrom(type))
            {
                var logger = _loggerFactory.CreateLogger(type.Name);
                IProxyPlugin? result = Activator.CreateInstance(type, [pluginEvents, context, logger, urlsToWatch, configSection]) as IProxyPlugin;
                if (result is not null && result.Name == pluginReference.Name)
                {
                    return result;
                }
            }
        }

        string availableTypes = string.Join(",", assembly.GetTypes().Select(t => t.FullName));
        throw new ApplicationException(
            $"Can't find plugin {pluginReference.Name} which implements IProxyPlugin in {assembly} from {AppContext.BaseDirectory}.\r\n" +
            $"Available types: {availableTypes}");
    }

    public static UrlToWatch ConvertToRegex(string stringMatcher)
    {
        var exclude = false;
        if (stringMatcher.StartsWith('!'))
        {
            exclude = true;
            stringMatcher = stringMatcher[1..];
        }

        return new UrlToWatch(
            new Regex($"^{Regex.Escape(stringMatcher).Replace("\\*", ".*")}$", RegexOptions.Compiled | RegexOptions.IgnoreCase),
            exclude
        );
    }

    private PluginConfig PluginConfig
    {
        get
        {
            if (_pluginConfig == null)
            {
                _pluginConfig = new PluginConfig();
                Configuration.Bind(_pluginConfig);

                if (ProxyHost.UrlsToWatch is not null && ProxyHost.UrlsToWatch.Any())
                {
                    _pluginConfig.UrlsToWatch = ProxyHost.UrlsToWatch.ToList();
                }
            }
            if (_pluginConfig == null || _pluginConfig.Plugins.Count == 0)
            {
                throw new InvalidDataException("The configuration must contain at least one plugin");
            }
            return _pluginConfig;
        }
    }

    private IConfigurationRoot Configuration { get => ConfigurationFactory.Value; }

    private readonly Lazy<IConfigurationRoot> ConfigurationFactory = new(() =>
        new ConfigurationBuilder()
                .AddJsonFile(ProxyHost.ConfigFile, optional: true, reloadOnChange: true)
                .Build()
    );
}

internal class PluginConfig
{
    public List<PluginReference> Plugins { get; set; } = [];
    public List<string> UrlsToWatch { get; set; } = [];
}

internal class PluginReference
{
    public bool Enabled { get; set; } = true;
    public string? ConfigSection { get; set; }
    public string PluginPath { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public List<string>? UrlsToWatch { get; set; }
}
