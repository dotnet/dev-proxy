using DevProxy.Abstractions.Plugins;
using DevProxy.Abstractions.Proxy;
using DevProxy.Abstractions.Utils;
using System.CommandLine;
using System.CommandLine.Help;
using System.CommandLine.Parsing;
using System.Globalization;

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
    internal const string ApiPortOptionName = "--api-port";
    internal const string IpAddressOptionName = "--ip-address";
    internal const string LogLevelOptionName = "--log-level";
    internal const string RecordOptionName = "--record";
    internal const string WatchPidsOptionName = "--watch-pids";
    internal const string WatchProcessNamesOptionName = "--watch-process-names";
    internal const string ConfigFileOptionName = "--config-file";
    internal static readonly Option<string?> ConfigFileOption = new(ConfigFileOptionName, "-c")
    {
        HelpName = "config-file",
        Description = "The path to the configuration file"
    };
    internal const string NoFirstRunOptionName = "--no-first-run";
    internal const string AsSystemProxyOptionName = "--as-system-proxy";
    internal const string InstallCertOptionName = "--install-cert";
    internal const string UrlsToWatchOptionName = "--urls-to-watch";
    internal const string TimeoutOptionName = "--timeout";
    internal const string DiscoverOptionName = "--discover";
    internal const string EnvOptionName = "--env";
    internal const string OutputOptionName = "--output";
    internal const string DetachedOptionName = "--detach";
    internal const string InternalDaemonOptionName = "--_internal-daemon";

    private static readonly string[] globalOptions = ["--version"];
    private static readonly string[] helpOptions = ["--help", "-h", "/h", "-?", "/?"];

    private static bool _hasGlobalOptionsResolved;
    private static bool _isStdioCommandResolved;
    private static bool _isJwtCommandResolved;
    private static bool _isRootCommandResolved;
    private static bool _isDetachedModeResolved;
    private static bool _isInternalDaemonResolved;
    private static bool _stdioLogFilePathResolved;
    private static bool _detachedLogFilePathResolved;

    public static bool HasGlobalOptions
    {
        get
        {
            if (_hasGlobalOptionsResolved)
            {
                return field;
            }

            var args = Environment.GetCommandLineArgs();
            field = args.Any(arg => globalOptions.Contains(arg)) ||
                                args.Any(arg => helpOptions.Contains(arg));
            _hasGlobalOptionsResolved = true;
            return field;
        }
    }

    public static bool IsStdioCommand
    {
        get
        {
            if (_isStdioCommandResolved)
            {
                return field;
            }

            var args = Environment.GetCommandLineArgs();
            field = args.Contains("stdio");
            _isStdioCommandResolved = true;
            return field;
        }
    }

    public static bool IsJwtCommand
    {
        get
        {
            if (_isJwtCommandResolved)
            {
                return field;
            }

            var args = Environment.GetCommandLineArgs();
            field = args.Length > 1 && string.Equals(args[1], "jwt", StringComparison.OrdinalIgnoreCase);
            _isJwtCommandResolved = true;
            return field;
        }
    }

    public static bool IsDetachedMode
    {
        get
        {
            if (_isDetachedModeResolved)
            {
                return field;
            }

            var args = Environment.GetCommandLineArgs();
            field = args.Contains("--detach") || args.Contains("-d");
            _isDetachedModeResolved = true;
            return field;
        }
    }

    /// <summary>
    /// Determines if the root command (proxy itself) is being invoked.
    /// Returns true when no subcommand is specified (only options or no args).
    /// A subcommand is detected when the first non-program argument doesn't start with '-'.
    /// </summary>
    public static bool IsRootCommand
    {
        get
        {
            if (_isRootCommandResolved)
            {
                return field;
            }

            var args = Environment.GetCommandLineArgs();
            // Skip the first argument which is the program name
            // If there are no more arguments, it's the root command
            // If the first argument starts with '-', it's an option (root command)
            // Otherwise, it's a subcommand name
            field = args.Length <= 1 || args[1].StartsWith('-');
            _isRootCommandResolved = true;
            return field;
        }
    }

    public static bool IsInternalDaemon
    {
        get
        {
            if (_isInternalDaemonResolved)
            {
                return field;
            }

            var args = Environment.GetCommandLineArgs();
            field = args.Contains("--_internal-daemon");
            _isInternalDaemonResolved = true;
            return field;
        }
    }

    public static string StdioLogFilePath
    {
        get
        {
            if (_stdioLogFilePathResolved)
            {
                return field ?? string.Empty;
            }

            field = Path.Combine(
                Directory.GetCurrentDirectory(),
                $"devproxy-stdio-{DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}.log");
            _stdioLogFilePathResolved = true;
            return field;
        }
    }

    public static string DetachedLogFilePath
    {
        get
        {
            if (_detachedLogFilePathResolved)
            {
                return field ?? string.Empty;
            }

            field = State.StateManager.GenerateLogFilePath();
            _detachedLogFilePathResolved = true;
            return field;
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
        // Use special parsing for stdio command to disable response file expansion (@file)
        // This allows npm package names like @devproxy/mcp to be passed through literally
        var parseResult = IsStdioCommand
            ? StdioCommand.ParseStdioArgs(this, args)
            : Parse(args);
        var exitCode = await parseResult.InvokeAsync(app.Lifetime.ApplicationStopping);

        // Return exit code 2 for input validation and parse errors to distinguish
        // them from runtime errors (exit code 1), following conventions from
        // curl, git, and others
        if (exitCode != 0 && parseResult.Errors.Count > 0)
        {
            return 2;
        }

        return exitCode;
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
        ConfigFileOption.Validators.Clear();
        ConfigFileOption.Validators.Add(input =>
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
            HelpName = "log-level",
            Recursive = true
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

        var apiPortOption = new Option<int?>(ApiPortOptionName)
        {
            Description = "The port for the Dev Proxy API to listen on",
            HelpName = "api-port"
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

        var outputOption = new Option<OutputFormat?>(OutputOptionName)
        {
            Description = $"Output format. Allowed values: {string.Join(", ", Enum.GetNames<OutputFormat>())}",
            HelpName = "format",
            Recursive = true
        };
        outputOption.Validators.Add(input =>
        {
            if (input.Tokens.Count == 0)
            {
                return;
            }
            if (!Enum.TryParse<OutputFormat>(input.Tokens[0].Value, true, out _))
            {
                input.AddError($"{input.Tokens[0].Value} is not a valid output format. Allowed values are: {string.Join(", ", Enum.GetNames<OutputFormat>())}");
            }
        });

        var detachedOption = new Option<bool?>(DetachedOptionName, "-d")
        {
            Description = "Run Dev Proxy in the background"
        };

        var internalDaemonOption = new Option<bool?>(InternalDaemonOptionName)
        {
            Description = "Internal use only - do not use directly",
            Hidden = true
        };

        var options = new List<Option>
        {
            apiPortOption,
            asSystemProxyOption,
            ConfigFileOption,
            detachedOption,
            discoverOption,
            envOption,
            installCertOption,
            internalDaemonOption,
            ipAddressOption,
            logLevelOption,
            noFirstRunOption,
            outputOption,
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
            ActivatorUtilities.CreateInstance<CertCommand>(_serviceProvider),
            ActivatorUtilities.CreateInstance<StdioCommand>(_serviceProvider),
            ActivatorUtilities.CreateInstance<StatusCommand>(_serviceProvider),
            ActivatorUtilities.CreateInstance<StopCommand>(_serviceProvider),
            ActivatorUtilities.CreateInstance<LogsCommand>(_serviceProvider)
        };
        commands.AddRange(_plugins.SelectMany(p => p.GetCommands()));
        this.AddCommands(commands.OrderByName());

        var helpOption = Options.OfType<HelpOption>().FirstOrDefault();
        if (helpOption?.Action is HelpAction helpAction)
        {
            helpOption.Action = new ExitCodeHelpAction(helpAction);
        }

        SetAction(InvokeAsync);
    }

    private void ConfigureFromOptions(ParseResult parseResult)
    {
        var port = parseResult.GetValueOrDefault<int?>(PortOptionName);
        if (port is not null)
        {
            _proxyConfiguration.Port = port.Value;
        }
        var apiPort = parseResult.GetValueOrDefault<int?>(ApiPortOptionName);
        if (apiPort is not null)
        {
            _proxyConfiguration.ApiPort = apiPort.Value;
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
        var output = parseResult.GetValueOrDefault<OutputFormat?>(OutputOptionName);
        if (output is not null)
        {
            _proxyConfiguration.Output = output.Value;
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