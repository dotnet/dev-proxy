// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Microsoft.DevProxy.Abstractions;
using Microsoft.VisualStudio.Threading;
using System.CommandLine;
using System.CommandLine.Invocation;

namespace Microsoft.DevProxy.CommandHandlers;

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
        ParseOptions(context);
        _pluginEvents.RaiseOptionsLoaded(new OptionsLoadedArgs(context, _options));
        await CheckForNewVersionAsync();

        try
        {
            var builder = WebApplication.CreateBuilder();
            builder.Logging.AddFilter("Microsoft.Hosting.*", LogLevel.Error);
            builder.Logging.AddFilter("Microsoft.AspNetCore.*", LogLevel.Error);

            builder.Services.AddSingleton<IProxyState, ProxyState>();
            builder.Services.AddSingleton<IProxyConfiguration, ProxyConfiguration>(sp => ConfigurationFactory.Value);
            builder.Services.AddSingleton(_pluginEvents);
            builder.Services.AddSingleton(_logger);
            builder.Services.AddSingleton(_urlsToWatch);
            builder.Services.AddHostedService<ProxyEngine>();

            builder.Services.AddControllers();
            builder.Services.AddEndpointsApiExplorer();
            builder.Services.AddSwaggerGen();

            builder.Services.Configure<RouteOptions>(options =>
            {
                options.LowercaseUrls = true;
            });

            builder.WebHost.ConfigureKestrel(options =>
            {
                options.ListenLocalhost(ConfigurationFactory.Value.ApiPort);
                _logger.LogInformation("Dev Proxy API listening on http://localhost:{Port}...", ConfigurationFactory.Value.ApiPort);
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
        var watchPids = context.ParseResult.GetValueForOption<IEnumerable<int>?>(ProxyHost.WatchPidsOptionName, _options);
        if (watchPids is not null)
        {
            Configuration.WatchPids = watchPids;
        }
        var watchProcessNames = context.ParseResult.GetValueForOption<IEnumerable<string>?>(ProxyHost.WatchProcessNamesOptionName, _options);
        if (watchProcessNames is not null)
        {
            Configuration.WatchProcessNames = watchProcessNames;
        }
        var rate = context.ParseResult.GetValueForOption<int?>(ProxyHost.RateOptionName, _options);
        if (rate is not null)
        {
            Configuration.Rate = rate.Value;
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

        return configObject;
    });
}
