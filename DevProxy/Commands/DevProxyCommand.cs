using DevProxy.Abstractions.Plugins;
using DevProxy.Abstractions.Proxy;
using DevProxy.Abstractions.Utils;
using System.CommandLine;
using System.CommandLine.Parsing;

namespace DevProxy.Commands;

sealed class DevProxyCommand : RootCommand
{
    private readonly IServiceProvider _serviceProvider;
    private readonly IEnumerable<IPlugin> _plugins;
    private readonly ILogger _logger;
    private readonly IProxyConfiguration _proxyConfiguration;
    private readonly ISet<UrlToWatch> _urlsToWatch;
    private readonly UpdateNotification _updateNotification;
    private WebApplication? _app;

    internal const string PortOptionName = "--port";
    internal const string IpAddressOptionName = "--ip-address";
    internal const string LogLevelOptionName = "--log-level";
    internal const string RecordOptionName = "--record";
    internal const string WatchPidsOptionName = "--watch-pids";
    internal const string WatchProcessNamesOptionName = "--watch-process-names";
    internal const string ConfigFileOptionName = "--config-file";
    internal const string NoFirstRunOptionName = "--no-first-run";
    internal const string AsSystemProxyOptionName = "--as-system-proxy";
    internal const string InstallCertOptionName = "--install-cert";
    internal const string UrlsToWatchOptionName = "--urls-to-watch";
    internal const string TimeoutOptionName = "--timeout";
    internal const string DiscoverOptionName = "--discover";
    internal const string EnvOptionName = "--env";

    private static readonly string[] globalOptions = ["--version"];
    private static readonly string[] helpOptions = ["--help", "-h", "/h", "-?", "/?"];

    private static bool _hasGlobalOptionsResolved;
    private static bool _hasGlobalOptions;
    public static bool HasGlobalOptions
    {
        get
        {
            if (_hasGlobalOptionsResolved)
            {
                return _hasGlobalOptions;
            }

            var args = Environment.GetCommandLineArgs();
            _hasGlobalOptions = args.Any(arg => globalOptions.Contains(arg)) ||
                                args.Any(arg => helpOptions.Contains(arg));
            _hasGlobalOptionsResolved = true;
            return _hasGlobalOptions;
        }
    }

    public DevProxyCommand(
        IEnumerable<IPlugin> plugins,
        ISet<UrlToWatch> urlsToWatch,
        IProxyConfiguration proxyConfiguration,
        IServiceProvider serviceProvider,
        UpdateNotification updateNotification,
        ILogger<DevProxyCommand> logger) : base("Start Dev Proxy")
    {
        _serviceProvider = serviceProvider;
        _plugins = plugins;
        _urlsToWatch = urlsToWatch;
        _proxyConfiguration = proxyConfiguration;
        _updateNotification = updateNotification;
        _logger = logger;

        ConfigureCommand();
    }

    public async Task<int> InvokeAsync(string[] args, WebApplication app)
    {
        _app = app;
        var parseResult = Parse(args);
        return await parseResult.InvokeAsync(app.Lifetime.ApplicationStopping);
    }

    private async Task<int> InvokeAsync(ParseResult parseResult, CancellationToken cancellationToken)
    {
        if (_app is null)
        {
            throw new InvalidOperationException("WebApplication instance is not set. Please provide it when invoking the command.");
        }
        if (!_plugins.Any())
        {
            _logger.LogError("You haven't configured any plugins. Please add plugins to your configuration file. Dev Proxy will exit.");
            return 1;
        }
        if (_urlsToWatch.Count == 0)
        {
            _logger.LogError("You haven't configured any URLs to watch. Please add URLs to your configuration file or use the --urls-to-watch option. Dev Proxy will exit.");
            return 1;
        }

        ConfigureFromOptions(parseResult);
        var optionsLoadedArgs = new OptionsLoadedArgs(parseResult);
        foreach (var plugin in _plugins.Where(p => p.Enabled))
        {
            plugin.OptionsLoaded(optionsLoadedArgs);
        }

        await CheckForNewVersionAsync();

        try
        {
            var ipAddress = parseResult.GetValue<string?>(IpAddressOptionName) ?? _proxyConfiguration.IPAddress;
            _logger.LogInformation("Dev Proxy API listening on http://{IPAddress}:{Port}...", ipAddress, _proxyConfiguration.ApiPort);
            await _app.RunAsync(cancellationToken);

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

    private void ConfigureCommand()
    {
        var configFileOption = new Option<string?>(ConfigFileOptionName, "-c")
        {
            HelpName = "config-file",
            Description = "The path to the configuration file"
        };
        configFileOption.Validators.Add(input =>
        {
            var filePath = ProxyUtils.ReplacePathTokens(input.Tokens[0].Value);
            if (string.IsNullOrEmpty(filePath))
            {
                return;
            }

            if (!File.Exists(filePath))
            {
                input.AddError($"Configuration file {filePath} does not exist");
            }
        });

        var ipAddressOption = new Option<string?>(IpAddressOptionName)
        {
            Description = "The IP address for the proxy to bind to",
            HelpName = "ip-address"
        };
        ipAddressOption.Validators.Add(input =>
        {
            if (!System.Net.IPAddress.TryParse(input.Tokens[0].Value, out _))
            {
                input.AddError($"{input.Tokens[0].Value} is not a valid IP address");
            }
        });

        var urlsToWatchOption = new Option<List<string>?>(UrlsToWatchOptionName, "-u")
        {
            AllowMultipleArgumentsPerToken = true,
            Arity = ArgumentArity.ZeroOrMore,
            Description = "The list of URLs to watch for requests",
            HelpName = "urls-to-watch",
        };

        var logLevelOption = new Option<LogLevel?>(LogLevelOptionName)
        {
            Description = $"Level of messages to log. Allowed values: {string.Join(", ", Enum.GetNames<LogLevel>())}",
            HelpName = "log-level"
        };
        logLevelOption.Validators.Add(input =>
        {
            if (!Enum.TryParse<LogLevel>(input.Tokens[0].Value, true, out _))
            {
                input.AddError($"{input.Tokens[0].Value} is not a valid log level. Allowed values are: {string.Join(", ", Enum.GetNames<LogLevel>())}");
            }
        });

        var portOption = new Option<int?>(PortOptionName, "-p")
        {
            Description = "The port for the proxy to listen on",
            HelpName = "port"
        };

        var recordOption = new Option<bool?>(RecordOptionName)
        {
            Description = "Use this option to record all request logs"
        };

        var watchPidsOption = new Option<IEnumerable<int>>(WatchPidsOptionName)
        {
            AllowMultipleArgumentsPerToken = true,
            Description = "The IDs of processes to watch for requests",
            HelpName = "pids"
        };

        var watchProcessNamesOption = new Option<IEnumerable<string>>(WatchProcessNamesOptionName)
        {
            AllowMultipleArgumentsPerToken = true,
            Description = "The names of processes to watch for requests",
            HelpName = "process-names",
        };

        var noFirstRunOption = new Option<bool?>(NoFirstRunOptionName)
        {
            Description = "Skip the first run experience"
        };

        var discoverOption = new Option<bool?>(DiscoverOptionName)
        {
            Description = "Run Dev Proxy in discovery mode"
        };

        var asSystemProxyOption = new Option<bool?>(AsSystemProxyOptionName)
        {
            Description = "Set Dev Proxy as the system proxy"
        };

        var installCertOption = new Option<bool?>(InstallCertOptionName)
        {
            Description = "Install self-signed certificate"
        };
        installCertOption.Validators.Add(input =>
        {
            try
            {
                var asSystemProxy = input.GetValue(asSystemProxyOption) ?? true;
                var installCert = input.GetValue(installCertOption) ?? true;
                if (asSystemProxy && !installCert)
                {
                    input.AddError($"Requires option '{AsSystemProxyOptionName}' to be 'false'");
                }
            }
            catch (InvalidOperationException ex)
            {
                input.AddError(ex.Message);
            }
        });

        var timeoutOption = new Option<long?>(TimeoutOptionName, "-t")
        {
            Description = "Time in seconds after which Dev Proxy exits. Resets when Dev Proxy intercepts a request.",
            HelpName = "timeout"
        };
        timeoutOption.Validators.Add(input =>
        {
            try
            {
                if (!long.TryParse(input.Tokens[0].Value, out var timeoutInput) || timeoutInput < 1)
                {
                    input.AddError($"{input.Tokens[0].Value} is not valid as a timeout value");
                }
            }
            catch (InvalidOperationException ex)
            {
                input.AddError(ex.Message);
            }
        });

        var envOption = new Option<string[]>(EnvOptionName, "-e")
        {
            AllowMultipleArgumentsPerToken = true,
            Arity = ArgumentArity.ZeroOrMore,
            Description = "Variables to set for the Dev Proxy process",
            HelpName = "env",
        };
        envOption.Validators.Add(input =>
        {
            try
            {
                var envVars = input.GetValue(envOption);
                if (envVars is null || envVars.Length == 0)
                {
                    return;
                }

                foreach (var envVar in envVars)
                {
                    // Split on first '=' only
                    var parts = envVar.Split('=', 2);
                    if (parts.Length != 2)
                    {
                        input.AddError($"Invalid environment variable format: '{envVar}'. Expected format is 'name=value'.");
                        return;
                    }
                }
            }
            catch (InvalidOperationException ex)
            {
                input.AddError(ex.Message);
            }
        });

        var options = new List<Option>
        {
            asSystemProxyOption,
            configFileOption,
            discoverOption,
            envOption,
            installCertOption,
            ipAddressOption,
            logLevelOption,
            noFirstRunOption,
            portOption,
            recordOption,
            timeoutOption,
            urlsToWatchOption,
            watchPidsOption,
            watchProcessNamesOption
        };
        options.AddRange(_plugins
            .SelectMany(p => p.GetOptions())
            // remove duplicates by comparing the option names
            .GroupBy(o => o.Name)
            .Select(g => g.First()));
        this.AddOptions(options.OrderByName());

        var commands = new List<Command>
        {
            ActivatorUtilities.CreateInstance<MsGraphDbCommand>(_serviceProvider),
            ActivatorUtilities.CreateInstance<ConfigCommand>(_serviceProvider),
            ActivatorUtilities.CreateInstance<OutdatedCommand>(_serviceProvider),
            ActivatorUtilities.CreateInstance<JwtCommand>(_serviceProvider),
            ActivatorUtilities.CreateInstance<CertCommand>(_serviceProvider)
        };
        commands.AddRange(_plugins.SelectMany(p => p.GetCommands()));
        this.AddCommands(commands.OrderByName());

        SetAction(InvokeAsync);
    }

    private void ConfigureFromOptions(ParseResult parseResult)
    {
        var port = parseResult.GetValueOrDefault<int?>(PortOptionName);
        if (port is not null)
        {
            _proxyConfiguration.Port = port.Value;
        }
        var ipAddress = parseResult.GetValueOrDefault<string?>(IpAddressOptionName);
        if (ipAddress is not null)
        {
            _proxyConfiguration.IPAddress = ipAddress;
        }
        var record = parseResult.GetValueOrDefault<bool?>(RecordOptionName);
        if (record is not null)
        {
            _proxyConfiguration.Record = record.Value;
        }
        var watchPids = parseResult.GetValueOrDefault<IEnumerable<int>>(WatchPidsOptionName);
        if (watchPids is not null && watchPids.Any())
        {
            _proxyConfiguration.WatchPids = watchPids;
        }
        var watchProcessNames = parseResult.GetValueOrDefault<IEnumerable<string>>(WatchProcessNamesOptionName);
        if (watchProcessNames is not null && watchProcessNames.Any())
        {
            _proxyConfiguration.WatchProcessNames = watchProcessNames;
        }
        var noFirstRun = parseResult.GetValueOrDefault<bool?>(NoFirstRunOptionName);
        if (noFirstRun is not null)
        {
            _proxyConfiguration.NoFirstRun = noFirstRun.Value;
        }
        var asSystemProxy = parseResult.GetValueOrDefault<bool?>(AsSystemProxyOptionName);
        if (asSystemProxy is not null)
        {
            _proxyConfiguration.AsSystemProxy = asSystemProxy.Value;
        }
        var installCert = parseResult.GetValueOrDefault<bool?>(InstallCertOptionName);
        if (installCert is not null)
        {
            _proxyConfiguration.InstallCert = installCert.Value;
        }
        var timeout = parseResult.GetValueOrDefault<long?>(TimeoutOptionName);
        if (timeout is not null)
        {
            _proxyConfiguration.TimeoutSeconds = timeout.Value;
        }
        var isDiscover = parseResult.GetValueOrDefault<bool?>(DiscoverOptionName);
        if (isDiscover is not null)
        {
            _proxyConfiguration.Record = true;
        }
        var env = parseResult.GetValueOrDefault<string[]?>(EnvOptionName);
        if (env is not null)
        {
            _proxyConfiguration.Env = env.Select(static e =>
            {
                // Split on first '=' only
                var parts = e.Split('=', 2);
                return parts.Length != 2
                    ? throw new ArgumentException($"Invalid env format: {e}. Expected format is 'key=value'.")
                    : new KeyValuePair<string, string>(parts[0], parts[1]);
            }).ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
        }
    }

    private async Task CheckForNewVersionAsync()
    {
        var newReleaseInfo = await _updateNotification.CheckForNewVersionAsync(_proxyConfiguration.NewVersionNotification);
        if (newReleaseInfo != null)
        {
            _logger.LogInformation(
                "New Dev Proxy version {Version} is available.{NewLine}See https://aka.ms/devproxy/upgrade for more information.",
                newReleaseInfo.Version,
                Environment.NewLine
            );
        }
    }
}