// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Abstractions;
using Microsoft.VisualStudio.Threading;
using System.CommandLine;
using System.CommandLine.Invocation;
using System.Net;

namespace DevProxy.CommandHandlers;

public class ProxyCommandHandler(IPluginEvents pluginEvents,
                           Option[] options,
                           ISet<UrlToWatch> urlsToWatch,
                           ILogger logger) : ICommandHandler
{
    private readonly IPluginEvents _pluginEvents = pluginEvents ?? throw new ArgumentNullException(nameof(pluginEvents));
    private readonly Option[] _options = options ?? throw new ArgumentNullException(nameof(options));
    private readonly ISet<UrlToWatch> _urlsToWatch = urlsToWatch ?? throw new ArgumentNullException(nameof(urlsToWatch));
    private readonly ILogger _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    public static ProxyConfiguration Configuration { get => ConfigurationFactory.Value; }

    public int Invoke(InvocationContext context)
    {
        var joinableTaskContext = new JoinableTaskContext();
        var joinableTaskFactory = new JoinableTaskFactory(joinableTaskContext);

        return joinableTaskFactory.Run(async () => await InvokeAsync(context));
    }

    public async Task<int> InvokeAsync(InvocationContext context)
    {
        if (Configuration.Plugins.Count == 0)
        {
            _logger.LogWarning("You haven't configured any plugins. Please add plugins to your configuration file. Dev Proxy will exit.");
            return 1;
        }
        if (Configuration.UrlsToWatch.Count == 0)
        {
            _logger.LogWarning("You haven't configured any URLs to watch. Please add URLs to your configuration file or use the --urls-to-watch option. Dev Proxy will exit.");
            return 1;
        }

        ParseOptions(context);
        _pluginEvents.RaiseOptionsLoaded(new OptionsLoadedArgs(context, _options));
        await CheckForNewVersionAsync();

        try
        {
            var builder = WebApplication.CreateBuilder();
            builder.Logging.AddFilter("Microsoft.Hosting.*", LogLevel.Error);
            builder.Logging.AddFilter("Microsoft.AspNetCore.*", LogLevel.Error);

            // API controller is registered first and so is the last service to be disposed of when the app is shutdown
            builder.Services.AddControllers();

            builder.Services.AddSingleton<IProxyState, ProxyState>();
            builder.Services.AddSingleton<IProxyConfiguration, ProxyConfiguration>(sp => ConfigurationFactory.Value);
            builder.Services.AddSingleton(_pluginEvents);
            builder.Services.AddSingleton(_logger);
            builder.Services.AddSingleton(_urlsToWatch);
            builder.Services.AddHostedService<ProxyEngine>();

            builder.Services.AddEndpointsApiExplorer();
            builder.Services.AddSwaggerGen();

            builder.Services.Configure<RouteOptions>(options =>
            {
                options.LowercaseUrls = true;
            });

            var ipAddress = context.ParseResult.GetValueForOption<string?>(ProxyHost.IpAddressOptionName, _options);
            ipAddress ??= "127.0.0.1";
            builder.WebHost.ConfigureKestrel(options =>
            {
                options.Listen(IPAddress.Parse(ipAddress), ConfigurationFactory.Value.ApiPort);
                _logger.LogInformation("Dev Proxy API listening on http://{IPAddress}:{Port}...", ipAddress, ConfigurationFactory.Value.ApiPort);
            });

            var app = builder.Build();
            app.UseSwagger();
            app.UseSwaggerUI();
            app.MapControllers();
            await app.RunAsync();

            return 0;
        }
        catch (TaskCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "An error occurred while running Dev Proxy");
            var inner = ex.InnerException;

            while (inner is not null)
            {
                _logger.LogError(inner, "============ Inner exception ============");
                inner = inner.InnerException;
            }
#if DEBUG
            throw; // so debug tools go straight to the source of the exception when attached
#else
            return 1;
#endif
        }
    }

    private void ParseOptions(InvocationContext context)
    {
        var port = context.ParseResult.GetValueForOption<int?>(ProxyHost.PortOptionName, _options);
        if (port is not null)
        {
            Configuration.Port = port.Value;
        }
        var ipAddress = context.ParseResult.GetValueForOption<string?>(ProxyHost.IpAddressOptionName, _options);
        if (ipAddress is not null)
        {
            Configuration.IPAddress = ipAddress;
        }
        var record = context.ParseResult.GetValueForOption<bool?>(ProxyHost.RecordOptionName, _options);
        if (record is not null)
        {
            Configuration.Record = record.Value;
        }
        var watchPids = context.ParseResult.GetValueForOption<IEnumerable<int>>(ProxyHost.WatchPidsOptionName, _options);
        if (watchPids is not null && watchPids.Any())
        {
            Configuration.WatchPids = watchPids;
        }
        var watchProcessNames = context.ParseResult.GetValueForOption<IEnumerable<string>>(ProxyHost.WatchProcessNamesOptionName, _options);
        if (watchProcessNames is not null && watchProcessNames.Any())
        {
            Configuration.WatchProcessNames = watchProcessNames;
        }
        var noFirstRun = context.ParseResult.GetValueForOption<bool?>(ProxyHost.NoFirstRunOptionName, _options);
        if (noFirstRun is not null)
        {
            Configuration.NoFirstRun = noFirstRun.Value;
        }
        var asSystemProxy = context.ParseResult.GetValueForOption<bool?>(ProxyHost.AsSystemProxyOptionName, _options);
        if (asSystemProxy is not null)
        {
            Configuration.AsSystemProxy = asSystemProxy.Value;
        }
        var installCert = context.ParseResult.GetValueForOption<bool?>(ProxyHost.InstallCertOptionName, _options);
        if (installCert is not null)
        {
            Configuration.InstallCert = installCert.Value;
        }
        var timeout = context.ParseResult.GetValueForOption<long?>(ProxyHost.TimeoutOptionName, _options);
        if (timeout is not null)
        {
            Configuration.TimeoutSeconds = timeout.Value;
        }
        var isDiscover = context.ParseResult.GetValueForOption<bool?>(ProxyHost.DiscoverOptionName, _options);
        if (isDiscover is not null)
        {
            Configuration.Record = true;
        }
        var env = context.ParseResult.GetValueForOption<string[]?>(ProxyHost.EnvOptionName, _options);
        if (env is not null)
        {
            Configuration.Env = env.Select(e =>
            {
                // Split on first '=' only
                var parts = e.Split('=', 2);
                if (parts.Length != 2)
                {
                    throw new ArgumentException($"Invalid env format: {e}. Expected format is 'key=value'.");
                }
                return new KeyValuePair<string, string>(parts[0], parts[1]);
            }).ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
        }
    }

    private async Task CheckForNewVersionAsync()
    {
        var newReleaseInfo = await UpdateNotification.CheckForNewVersionAsync(Configuration.NewVersionNotification);
        if (newReleaseInfo != null)
        {
            _logger.LogInformation(
                "New Dev Proxy version {version} is available.{newLine}See https://aka.ms/devproxy/upgrade for more information.",
                newReleaseInfo.Version,
                Environment.NewLine
            );
        }
    }

    private static readonly Lazy<ProxyConfiguration> ConfigurationFactory = new(() =>
    {
        var builder = new ConfigurationBuilder();
        var configuration = builder
            .AddJsonFile(ProxyHost.ConfigFile, optional: true, reloadOnChange: true)
            .Build();
        var configObject = new ProxyConfiguration();
        configuration.Bind(configObject);

        // Custom binding for internal properties, because it's not happening
        // automatically
        var pluginsSection = configuration.GetSection("Plugins");
        if (pluginsSection.Exists())
        {
            configObject.Plugins = pluginsSection.Get<List<PluginReference>>() ?? [];
        }

        var urlsSection = configuration.GetSection("UrlsToWatch");
        if (urlsSection.Exists())
        {
            configObject.UrlsToWatch = urlsSection.Get<List<string>>() ?? [];
        }

        return configObject;
    });
}
