using DevProxy.Abstractions.Plugins;
using DevProxy.Abstractions.Proxy;
using DevProxy.Abstractions.Utils;
using System.CommandLine;
using System.CommandLine.Parsing;
using Microsoft.VisualStudio.Threading;

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
    private new IReadOnlyList<Option> Options = Array.Empty<Option>();

    internal const string PortOptionName = "--port";
    private Option<int?>? _portOption;
    internal const string IpAddressOptionName = "--ip-address";
    private static Option<string?>? _ipAddressOption;
    internal const string LogLevelOptionName = "--log-level";
    private static Option<LogLevel?>? _logLevelOption;
    internal const string RecordOptionName = "--record";
    private Option<bool?>? _recordOption;
    internal const string WatchPidsOptionName = "--watch-pids";
    private Option<IEnumerable<int>>? _watchPidsOption;
    internal const string WatchProcessNamesOptionName = "--watch-process-names";
    private Option<IEnumerable<string>>? _watchProcessNamesOption;
    internal const string ConfigFileOptionName = "--config-file";
    private static Option<string?>? _configFileOption;
    internal const string NoFirstRunOptionName = "--no-first-run";
    private Option<bool?>? _noFirstRunOption;
    internal const string AsSystemProxyOptionName = "--as-system-proxy";
    private Option<bool?>? _asSystemProxyOption;
    internal const string InstallCertOptionName = "--install-cert";
    private Option<bool?>? _installCertOption;
    internal const string UrlsToWatchOptionName = "--urls-to-watch";
    private static Option<List<string>?>? _urlsToWatchOption;
    internal const string TimeoutOptionName = "--timeout";
    private Option<long?>? _timeoutOption;
    internal const string DiscoverOptionName = "--discover";
    private Option<bool?>? _discoverOption;
    internal const string EnvOptionName = "--env";
    private Option<string[]?>? _envOption;

    public static string? ConfigFile
    {
        get
        {
            if (_configFileOption is null)
            {
                _configFileOption = new Option<string?>(ConfigFileOptionName, ["-c"])
                {
                    Description = "The path to the configuration file",
                    HelpName = "configFile"
                };
                
                // TODO: Fix validation for beta5
                // _configFileOption.Validators.Add(input => { ... });
            }

            // TODO: Fix early parsing for beta5 - Options no longer have Parse method
            // var result = _configFileOption.Parse(Environment.GetCommandLineArgs());
            // // since we're parsing all args, and other options are not instantiated yet
            // // we're getting here a bunch of other errors, so we only need to look for
            // // errors related to the config file option
            // var error = result.Errors.FirstOrDefault(e => e.SymbolResult?.Symbol == _configFileOption);
            // if (error is not null)
            // {
            //     // Logger is not available here yet so we need to fallback to Console
            //     var color = Console.ForegroundColor;
            //     Console.ForegroundColor = ConsoleColor.Red;
            //     Console.Error.WriteLine(error.Message);
            //     Console.ForegroundColor = color;
            //     Environment.Exit(1);
            // }

            var configFile = Environment.GetCommandLineArgs()
                .Where(arg => arg.StartsWith("--config-file=", StringComparison.Ordinal) || arg.StartsWith("-c=", StringComparison.Ordinal))
                .FirstOrDefault()?.Split('=', 2).LastOrDefault();
            return configFile is not null ?
                Path.GetFullPath(ProxyUtils.ReplacePathTokens(configFile)) :
                null;
        }
    }

    private static bool _logLevelResolved;
    private static LogLevel? _logLevel;
    public static LogLevel? LogLevel
    {
        get
        {
            if (_logLevelResolved)
            {
                return _logLevel;
            }

            if (_logLevelOption is null)
            {
                _logLevelOption = new Option<LogLevel?>(
                    LogLevelOptionName,
                    []
                )
                {
                    Description = $"Level of messages to log. Allowed values: {string.Join(", ", Enum.GetNames<LogLevel>())}",
                    HelpName = "logLevel"
                };
                
                // TODO: Fix validation for beta5  
                // _logLevelOption.Validators.Add(input => { ... });
            }

            // TODO: Fix early parsing for beta5 - Options no longer have Parse method  
            // var result = _logLevelOption.Parse(Environment.GetCommandLineArgs());
            // // since we're parsing all args, and other options are not instantiated yet
            // // we're getting here a bunch of other errors, so we only need to look for
            // // errors related to the log level option
            // var error = result.Errors.FirstOrDefault(e => e.SymbolResult?.Symbol == _logLevelOption);
            // if (error is not null)
            // {
            //     // Logger is not available here yet so we need to fallback to Console
            //     var color = Console.ForegroundColor;
            //     Console.ForegroundColor = ConsoleColor.Red;
            //     Console.Error.WriteLine(error.Message);
            //     Console.ForegroundColor = color;
            //     Environment.Exit(1);
            // }

            // TODO: Fix log level extraction for beta5
            _logLevel = null; // Default fallback until parsing is fixed
            _logLevelResolved = true;

            return _logLevel;
        }
    }

    private static bool _ipAddressResolved;
    private static string? _ipAddress;
    public static string? IPAddress
    {
        get
        {
            if (_ipAddressResolved)
            {
                return _ipAddress;
            }

            if (_ipAddressOption is null)
            {
                _ipAddressOption = new(IpAddressOptionName, [])
                {
                    Description = "The IP address for the proxy to bind to",
                    HelpName = "ipAddress"
                };
                
                // TODO: Fix validation for beta5
                // _ipAddressOption.Validators.Add(input => { ... });
            }

            // TODO: Fix early parsing for beta5 - Options no longer have Parse method
            // var result = _ipAddressOption.Parse(Environment.GetCommandLineArgs());
            // // since we're parsing all args, and other options are not instantiated yet
            // // we're getting here a bunch of other errors, so we only need to look for
            // // errors related to the log level option
            // var error = result.Errors.FirstOrDefault(e => e.SymbolResult?.Symbol == _ipAddressOption);
            // if (error is not null)
            // {
            //     // Logger is not available here yet so we need to fallback to Console
            //     var color = Console.ForegroundColor;
            //     Console.ForegroundColor = ConsoleColor.Red;
            //     Console.Error.WriteLine(error.Message);
            //     Console.ForegroundColor = color;
            //     Environment.Exit(1);
            // }

            // TODO: Fix IP address extraction for beta5  
            _ipAddress = null; // Default fallback until parsing is fixed
            _ipAddressResolved = true;

            return _ipAddress;
        }
    }

    private static bool urlsToWatchResolved;
    private static List<string>? urlsToWatch;
    public static List<string>? UrlsToWatch
    {
        get
        {
            if (urlsToWatchResolved)
            {
                return urlsToWatch;
            }

            if (_urlsToWatchOption is null)
            {
                _urlsToWatchOption = new Option<List<string>?>(
                    UrlsToWatchOptionName,
                    ["-u"]
                )
                {
                    Description = "The list of URLs to watch for requests",
                    HelpName = "urlsToWatch",
                    AllowMultipleArgumentsPerToken = true,
                    Arity = ArgumentArity.ZeroOrMore
                };
            }

            // TODO: Fix early parsing for beta5 - Options no longer have Parse method
            // var result = _urlsToWatchOption!.Parse(Environment.GetCommandLineArgs());
            // since we're parsing all args, and other options are not instantiated yet
            // we're getting here a bunch of other errors, so we only need to look for
            // errors related to the log level option
            // var error = result.Errors.FirstOrDefault(e => e.SymbolResult?.Symbol == _urlsToWatchOption);
            // if (error is not null) { ... }
            // TODO: Complete removal of early parsing error handling  
            // (leftover code from previous parsing approach)

            // TODO: Fix URLs to watch extraction for beta5
            urlsToWatch = null; // Default fallback until parsing is fixed
            
            // TODO: Remove dead code when early parsing is restored
            // if (urlsToWatch is not null && urlsToWatch.Count == 0)
            // {
            //     urlsToWatch = null;
            // }
            urlsToWatchResolved = true;

            return urlsToWatch;
        }
    }

    private static bool _isRootCommandResolved;
    private static bool _isRootCommand;
    public static bool IsRootCommand
    {
        get
        {
            if (_isRootCommandResolved)
            {
                return _isRootCommand;
            }

            // Check if the command is being invoked as the root command
            // by checking if the second argument is an option
            var args = Environment.GetCommandLineArgs();
            _isRootCommand = args.Length == 1 || args[1].StartsWith('-');
            _isRootCommandResolved = true;
            return _isRootCommand;
        }
    }

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
        ILogger<DevProxyCommand> logger)
    {
        _plugins = plugins;
        _urlsToWatch = urlsToWatch;
        _proxyConfiguration = proxyConfiguration;
        _serviceProvider = serviceProvider;
        _logger = logger;
        _updateNotification = updateNotification;

        ConfigureCommand();
    }

    private void ConfigureCommand()
    {
        _portOption = new(PortOptionName, ["-p"])
        {
            Description = "The port for the proxy to listen on",
            HelpName = "port"
        };

        _recordOption = new(RecordOptionName, [])
        {
            Description = "Use this option to record all request logs"
        };

        _watchPidsOption = new(WatchPidsOptionName, [])
        {
            Description = "The IDs of processes to watch for requests",
            HelpName = "pids",
            AllowMultipleArgumentsPerToken = true
        };

        _watchProcessNamesOption = new(WatchProcessNamesOptionName, [])
        {
            Description = "The names of processes to watch for requests",
            HelpName = "processNames",
            AllowMultipleArgumentsPerToken = true
        };

        _noFirstRunOption = new(NoFirstRunOptionName, "Skip the first run experience");

        _discoverOption = new(DiscoverOptionName, "Run Dev Proxy in discovery mode");

        _asSystemProxyOption = new(AsSystemProxyOptionName, [])
        {
            Description = "Set Dev Proxy as the system proxy"
        };
        
        // TODO: Fix validation for beta5
        // _asSystemProxyOption.Validators.Add(input => { ... });

        _installCertOption = new(InstallCertOptionName, [])
        {
            Description = "Install self-signed certificate"  
        };
        
        // TODO: Fix validation for beta5  
        // _installCertOption.Validators.Add(input => { ... });

        _timeoutOption = new(TimeoutOptionName, ["-t"])
        {
            Description = "Time in seconds after which Dev Proxy exits. Resets when Dev Proxy intercepts a request.",
            HelpName = "timeout"
        };
        
        // TODO: Fix validation for beta5
        // _timeoutOption.Validators.Add(input => { ... });

        _envOption = new(EnvOptionName, "Variables to set for the Dev Proxy process")
        {
            HelpName = "env",
            AllowMultipleArgumentsPerToken = true,
            Arity = ArgumentArity.ZeroOrMore
        };
        // TODO: Fix validation and alias for beta5
        // _envOption.AddAlias("-e");
        // _envOption.Validators.Add(input => { ... });

        var options = new List<Option>
        {
            _portOption,
            _ipAddressOption!,
            _recordOption,
            _watchPidsOption,
            _watchProcessNamesOption,
            // _configFileOption is set during DI, so it's always set here
            _configFileOption!,
            _noFirstRunOption,
            _asSystemProxyOption,
            _installCertOption,
            // accessed during setup stage so defined by here
            _urlsToWatchOption!,
            _timeoutOption,
            _discoverOption,
            _envOption
        };
        options.AddRange(_plugins
            .SelectMany(p => p.GetOptions())
            // remove duplicates by comparing the option names
            .GroupBy(o => o.Name)
            .Select(g => g.First()));
        this.AddOptions(options.OrderByName());

        // Store options for use in parsing methods
        Options = options.AsReadOnly();

        // Add log level as a regular option instead of global option
        // In beta5, global options are handled by adding to the root command

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

        this.SetAction(InvokeAsync);
    }

    public async Task<int> InvokeAsync(string[] args, WebApplication app)
    {
        _app = app;
        var parseResult = this.Parse(args);
        return await parseResult.InvokeAsync();
    }

    private async Task<int> InvokeAsync(ParseResult parseResult)
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

        ParseOptions(parseResult);
        var optionsLoadedArgs = new OptionsLoadedArgs(parseResult, Options);
        foreach (var plugin in _plugins.Where(p => p.Enabled))
        {
            plugin.OptionsLoaded(optionsLoadedArgs);
        }

        await CheckForNewVersionAsync();

        try
        {
            var ipAddress = IPAddress ?? _proxyConfiguration.IPAddress;
            _logger.LogInformation("Dev Proxy API listening on http://{IPAddress}:{Port}...", ipAddress, _proxyConfiguration.ApiPort);
            await _app.RunAsync();

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

    private void ParseOptions(ParseResult parseResult)
    {
        var port = parseResult.GetValueForOption<int?>(PortOptionName, Options);
        if (port is not null)
        {
            _proxyConfiguration.Port = port.Value;
        }
        var ipAddress = parseResult.GetValueForOption<string?>(IpAddressOptionName, Options);
        if (ipAddress is not null)
        {
            _proxyConfiguration.IPAddress = ipAddress;
        }
        var record = parseResult.GetValueForOption<bool?>(RecordOptionName, Options);
        if (record is not null)
        {
            _proxyConfiguration.Record = record.Value;
        }
        var watchPids = parseResult.GetValueForOption<IEnumerable<int>>(WatchPidsOptionName, Options);
        if (watchPids is not null && watchPids.Any())
        {
            _proxyConfiguration.WatchPids = watchPids;
        }
        var watchProcessNames = parseResult.GetValueForOption<IEnumerable<string>>(WatchProcessNamesOptionName, Options);
        if (watchProcessNames is not null && watchProcessNames.Any())
        {
            _proxyConfiguration.WatchProcessNames = watchProcessNames;
        }
        var noFirstRun = parseResult.GetValueForOption<bool?>(NoFirstRunOptionName, Options);
        if (noFirstRun is not null)
        {
            _proxyConfiguration.NoFirstRun = noFirstRun.Value;
        }
        var asSystemProxy = parseResult.GetValueForOption<bool?>(AsSystemProxyOptionName, Options);
        if (asSystemProxy is not null)
        {
            _proxyConfiguration.AsSystemProxy = asSystemProxy.Value;
        }
        var installCert = parseResult.GetValueForOption<bool?>(InstallCertOptionName, Options);
        if (installCert is not null)
        {
            _proxyConfiguration.InstallCert = installCert.Value;
        }
        var timeout = parseResult.GetValueForOption<long?>(TimeoutOptionName, Options);
        if (timeout is not null)
        {
            _proxyConfiguration.TimeoutSeconds = timeout.Value;
        }
        var isDiscover = parseResult.GetValueForOption<bool?>(DiscoverOptionName, Options);
        if (isDiscover is not null)
        {
            _proxyConfiguration.Record = true;
        }
        var env = parseResult.GetValueForOption<string[]?>(EnvOptionName, Options);
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