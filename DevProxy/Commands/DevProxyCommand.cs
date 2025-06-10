using DevProxy.Abstractions.Plugins;
using DevProxy.Abstractions.Proxy;
using DevProxy.Abstractions.Utils;
using System.CommandLine;
using System.CommandLine.Invocation;
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
                _configFileOption = new Option<string?>(ConfigFileOptionName, "The path to the configuration file");
                _configFileOption.AddAlias("-c");
                _configFileOption.ArgumentHelpName = "configFile";
                _configFileOption.AddValidator(input =>
                {
                    var filePath = ProxyUtils.ReplacePathTokens(input.Tokens[0].Value);
                    if (string.IsNullOrEmpty(filePath))
                    {
                        return;
                    }

                    if (!File.Exists(filePath))
                    {
                        input.ErrorMessage = $"Configuration file {filePath} does not exist";
                    }
                });
            }

            var result = _configFileOption.Parse(Environment.GetCommandLineArgs());
            // since we're parsing all args, and other options are not instantiated yet
            // we're getting here a bunch of other errors, so we only need to look for
            // errors related to the config file option
            var error = result.Errors.FirstOrDefault(e => e.SymbolResult?.Symbol == _configFileOption);
            if (error is not null)
            {
                // Logger is not available here yet so we need to fallback to Console
                var color = Console.ForegroundColor;
                Console.ForegroundColor = ConsoleColor.Red;
                Console.Error.WriteLine(error.Message);
                Console.ForegroundColor = color;
                Environment.Exit(1);
            }

            var configFile = result.GetValueForOption(_configFileOption);
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
                    $"Level of messages to log. Allowed values: {string.Join(", ", Enum.GetNames<LogLevel>())}"
                )
                {
                    ArgumentHelpName = "logLevel"
                };
                _logLevelOption.AddValidator(input =>
                {
                    if (!Enum.TryParse<LogLevel>(input.Tokens[0].Value, true, out _))
                    {
                        input.ErrorMessage = $"{input.Tokens[0].Value} is not a valid log level. Allowed values are: {string.Join(", ", Enum.GetNames<LogLevel>())}";
                    }
                });
            }

            var result = _logLevelOption.Parse(Environment.GetCommandLineArgs());
            // since we're parsing all args, and other options are not instantiated yet
            // we're getting here a bunch of other errors, so we only need to look for
            // errors related to the log level option
            var error = result.Errors.FirstOrDefault(e => e.SymbolResult?.Symbol == _logLevelOption);
            if (error is not null)
            {
                // Logger is not available here yet so we need to fallback to Console
                var color = Console.ForegroundColor;
                Console.ForegroundColor = ConsoleColor.Red;
                Console.Error.WriteLine(error.Message);
                Console.ForegroundColor = color;
                Environment.Exit(1);
            }

            _logLevel = result.GetValueForOption(_logLevelOption);
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
                _ipAddressOption = new(IpAddressOptionName, "The IP address for the proxy to bind to")
                {
                    ArgumentHelpName = "ipAddress"
                };
                _ipAddressOption.AddValidator(input =>
                {
                    if (!System.Net.IPAddress.TryParse(input.Tokens[0].Value, out _))
                    {
                        input.ErrorMessage = $"{input.Tokens[0].Value} is not a valid IP address";
                    }
                });
            }

            var result = _ipAddressOption.Parse(Environment.GetCommandLineArgs());
            // since we're parsing all args, and other options are not instantiated yet
            // we're getting here a bunch of other errors, so we only need to look for
            // errors related to the log level option
            var error = result.Errors.FirstOrDefault(e => e.SymbolResult?.Symbol == _ipAddressOption);
            if (error is not null)
            {
                // Logger is not available here yet so we need to fallback to Console
                var color = Console.ForegroundColor;
                Console.ForegroundColor = ConsoleColor.Red;
                Console.Error.WriteLine(error.Message);
                Console.ForegroundColor = color;
                Environment.Exit(1);
            }

            _ipAddress = result.GetValueForOption(_ipAddressOption);
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
                    "The list of URLs to watch for requests"
                )
                {
                    ArgumentHelpName = "urlsToWatch",
                    AllowMultipleArgumentsPerToken = true,
                    Arity = ArgumentArity.ZeroOrMore
                };
                _urlsToWatchOption.AddAlias("-u");
            }

            var result = _urlsToWatchOption!.Parse(Environment.GetCommandLineArgs());
            // since we're parsing all args, and other options are not instantiated yet
            // we're getting here a bunch of other errors, so we only need to look for
            // errors related to the log level option
            var error = result.Errors.FirstOrDefault(e => e.SymbolResult?.Symbol == _urlsToWatchOption);
            if (error is not null)
            {
                // Logger is not available here yet so we need to fallback to Console
                var color = Console.ForegroundColor;
                Console.ForegroundColor = ConsoleColor.Red;
                Console.Error.WriteLine(error.Message);
                Console.ForegroundColor = color;
                Environment.Exit(1);
            }

            urlsToWatch = result.GetValueForOption(_urlsToWatchOption!);
            if (urlsToWatch is not null && urlsToWatch.Count == 0)
            {
                urlsToWatch = null;
            }
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
        _portOption = new(PortOptionName, "The port for the proxy to listen on");
        _portOption.AddAlias("-p");
        _portOption.ArgumentHelpName = "port";

        _recordOption = new(RecordOptionName, "Use this option to record all request logs");

        _watchPidsOption = new(WatchPidsOptionName, "The IDs of processes to watch for requests")
        {
            ArgumentHelpName = "pids",
            AllowMultipleArgumentsPerToken = true
        };

        _watchProcessNamesOption = new(WatchProcessNamesOptionName, "The names of processes to watch for requests")
        {
            ArgumentHelpName = "processNames",
            AllowMultipleArgumentsPerToken = true
        };

        _noFirstRunOption = new(NoFirstRunOptionName, "Skip the first run experience");

        _discoverOption = new(DiscoverOptionName, "Run Dev Proxy in discovery mode");

        _asSystemProxyOption = new(AsSystemProxyOptionName, "Set Dev Proxy as the system proxy");
        _asSystemProxyOption.AddValidator(input =>
        {
            try
            {
                _ = input.GetValueForOption(_asSystemProxyOption);
            }
            catch (InvalidOperationException ex)
            {
                input.ErrorMessage = ex.Message;
            }
        });

        _installCertOption = new(InstallCertOptionName, "Install self-signed certificate");
        _installCertOption.AddValidator(input =>
        {
            try
            {
                var asSystemProxy = input.GetValueForOption(_asSystemProxyOption) ?? true;
                var installCert = input.GetValueForOption(_installCertOption) ?? true;
                if (asSystemProxy && !installCert)
                {
                    input.ErrorMessage = $"Requires option '--{_asSystemProxyOption.Name}' to be 'false'";
                }
            }
            catch (InvalidOperationException ex)
            {
                input.ErrorMessage = ex.Message;
            }
        });

        _timeoutOption = new(TimeoutOptionName, "Time in seconds after which Dev Proxy exits. Resets when Dev Proxy intercepts a request.")
        {
            ArgumentHelpName = "timeout",
        };
        _timeoutOption.AddValidator(input =>
        {
            try
            {
                if (!long.TryParse(input.Tokens[0].Value, out var timeoutInput) || timeoutInput < 1)
                {
                    input.ErrorMessage = $"{input.Tokens[0].Value} is not valid as a timeout value";
                }
            }
            catch (InvalidOperationException ex)
            {
                input.ErrorMessage = ex.Message;
            }
        });
        _timeoutOption.AddAlias("-t");

        _envOption = new(EnvOptionName, "Variables to set for the Dev Proxy process")
        {
            ArgumentHelpName = "env",
            AllowMultipleArgumentsPerToken = true,
            Arity = ArgumentArity.ZeroOrMore
        };
        _envOption.AddAlias("-e");
        _envOption.AddValidator(input =>
        {
            try
            {
                var envVars = input.GetValueForOption(_envOption);
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
                        input.ErrorMessage = $"Invalid environment variable format: '{envVar}'. Expected format is 'name=value'.";
                        return;
                    }
                }
            }
            catch (InvalidOperationException ex)
            {
                input.ErrorMessage = ex.Message;
            }
        });

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

        AddGlobalOption(_logLevelOption!);

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

        this.SetHandler(InvokeAsync);
    }

    public async Task<int> InvokeAsync(string[] args, WebApplication app)
    {
        _app = app;
        return await this.InvokeAsync(args);
    }

    private async Task<int> InvokeAsync(InvocationContext context)
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

        ParseOptions(context);
        var optionsLoadedArgs = new OptionsLoadedArgs(context, Options);
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

    private void ParseOptions(InvocationContext context)
    {
        var port = context.ParseResult.GetValueForOption<int?>(PortOptionName, Options);
        if (port is not null)
        {
            _proxyConfiguration.Port = port.Value;
        }
        var ipAddress = context.ParseResult.GetValueForOption<string?>(IpAddressOptionName, Options);
        if (ipAddress is not null)
        {
            _proxyConfiguration.IPAddress = ipAddress;
        }
        var record = context.ParseResult.GetValueForOption<bool?>(RecordOptionName, Options);
        if (record is not null)
        {
            _proxyConfiguration.Record = record.Value;
        }
        var watchPids = context.ParseResult.GetValueForOption<IEnumerable<int>>(WatchPidsOptionName, Options);
        if (watchPids is not null && watchPids.Any())
        {
            _proxyConfiguration.WatchPids = watchPids;
        }
        var watchProcessNames = context.ParseResult.GetValueForOption<IEnumerable<string>>(WatchProcessNamesOptionName, Options);
        if (watchProcessNames is not null && watchProcessNames.Any())
        {
            _proxyConfiguration.WatchProcessNames = watchProcessNames;
        }
        var noFirstRun = context.ParseResult.GetValueForOption<bool?>(NoFirstRunOptionName, Options);
        if (noFirstRun is not null)
        {
            _proxyConfiguration.NoFirstRun = noFirstRun.Value;
        }
        var asSystemProxy = context.ParseResult.GetValueForOption<bool?>(AsSystemProxyOptionName, Options);
        if (asSystemProxy is not null)
        {
            _proxyConfiguration.AsSystemProxy = asSystemProxy.Value;
        }
        var installCert = context.ParseResult.GetValueForOption<bool?>(InstallCertOptionName, Options);
        if (installCert is not null)
        {
            _proxyConfiguration.InstallCert = installCert.Value;
        }
        var timeout = context.ParseResult.GetValueForOption<long?>(TimeoutOptionName, Options);
        if (timeout is not null)
        {
            _proxyConfiguration.TimeoutSeconds = timeout.Value;
        }
        var isDiscover = context.ParseResult.GetValueForOption<bool?>(DiscoverOptionName, Options);
        if (isDiscover is not null)
        {
            _proxyConfiguration.Record = true;
        }
        var env = context.ParseResult.GetValueForOption<string[]?>(EnvOptionName, Options);
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