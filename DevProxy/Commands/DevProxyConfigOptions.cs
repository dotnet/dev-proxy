using DevProxy.Abstractions.Proxy;
using DevProxy.Abstractions.Utils;
using System.CommandLine;
using System.CommandLine.Parsing;

namespace DevProxy.Commands;

sealed class DevProxyConfigOptions : RootCommand
{
    public string? ConfigFile
    {
        get
        {
            var configFile = _parseResult?.GetValueOrDefault<string?>(DevProxyCommand.ConfigFileOptionName);
            return configFile is not null ?
                Path.GetFullPath(ProxyUtils.ReplacePathTokens(configFile)) :
                null;
        }
    }

    public int? ApiPort => _parseResult?.GetValueOrDefault<int?>(DevProxyCommand.ApiPortOptionName);
    public int? Port => _parseResult?.GetValueOrDefault<int?>(DevProxyCommand.PortOptionName);
    public bool Discover => _parseResult?.GetValueOrDefault<bool?>(DevProxyCommand.DiscoverOptionName) ?? false;
    public string? IPAddress => _parseResult?.GetValueOrDefault<string?>(DevProxyCommand.IpAddressOptionName);
    public bool IsStdioMode => _parseResult?.CommandResult.Command.Name == "stdio";
    public LogFor? LogFor => _parseResult?.GetValueOrDefault<LogFor?>(DevProxyCommand.LogForOptionName);
    public LogLevel? LogLevel => _parseResult?.GetValueOrDefault<LogLevel?>(DevProxyCommand.LogLevelOptionName);

    public List<string>? UrlsToWatch
    {
        get
        {
            var value = _parseResult?.GetValueOrDefault<List<string>?>(DevProxyCommand.UrlsToWatchOptionName);
            if (value is null || value.Count == 0)
            {
                return null;
            }

            return value;
        }
    }

    private ParseResult? _parseResult;

    public DevProxyConfigOptions()
    {
        // here we only use custom parsers for the initial options
        // general parsing and error handling is done in the DevProxyCommand
        var configFileOption = new Option<string?>(DevProxyCommand.ConfigFileOptionName, "-c")
        {
            CustomParser = result =>
            {
                if (!result.Tokens.Any())
                {
                    return null;
                }

                var value = result.Tokens[0].Value;
                if (string.IsNullOrEmpty(value))
                {
                    return null;
                }

                // Replace path tokens in the configuration file path
                value = ProxyUtils.ReplacePathTokens(value);
                return value;
            }
        };

        var ipAddressOption = new Option<string?>(DevProxyCommand.IpAddressOptionName)
        {
            CustomParser = result =>
            {
                if (!result.Tokens.Any())
                {
                    return null;
                }

                var value = result.Tokens[0].Value;
                if (string.IsNullOrEmpty(value))
                {
                    return null;
                }

                if (System.Net.IPAddress.TryParse(value, out _))
                {
                    return value;
                }
                else
                {
                    return null;
                }
            }
        };

        var urlsToWatchOption = new Option<List<string>?>(DevProxyCommand.UrlsToWatchOptionName, "-u")
        {
            AllowMultipleArgumentsPerToken = true,
            Arity = ArgumentArity.ZeroOrMore
        };

        var logLevelOption = new Option<LogLevel?>(DevProxyCommand.LogLevelOptionName)
        {
            CustomParser = result =>
            {
                if (!result.Tokens.Any())
                {
                    return null;
                }

                if (Enum.TryParse<LogLevel>(result.Tokens[0].Value, true, out var logLevel))
                {
                    return logLevel;
                }
                else
                {
                    return null;
                }
            }
        };

        var logForOption = new Option<LogFor?>(DevProxyCommand.LogForOptionName)
        {
            CustomParser = result =>
            {
                if (!result.Tokens.Any())
                {
                    return null;
                }

                if (Enum.TryParse<LogFor>(result.Tokens[0].Value, true, out var logFor))
                {
                    return logFor;
                }

                return null;
            }
        };

        var apiPortOption = new Option<int?>(DevProxyCommand.ApiPortOptionName);
        var portOption = new Option<int?>(DevProxyCommand.PortOptionName, "-p");

        var discoverOption = new Option<bool>(DevProxyCommand.DiscoverOptionName, "--discover")
        {
            Arity = ArgumentArity.Zero
        };

        var options = new List<Option>
        {
            apiPortOption,
            ipAddressOption,
            configFileOption,
            portOption,
            urlsToWatchOption,
            logForOption,
            logLevelOption,
            discoverOption
        };
        this.AddOptions(options.OrderByName());

        // Add stdio subcommand with config-file option so it can be parsed early
        var stdioConfigFileOption = new Option<string?>(DevProxyCommand.ConfigFileOptionName, "-c")
        {
            CustomParser = result =>
            {
                if (!result.Tokens.Any())
                {
                    return null;
                }

                var value = result.Tokens[0].Value;
                if (string.IsNullOrEmpty(value))
                {
                    return null;
                }

                value = ProxyUtils.ReplacePathTokens(value);
                return value;
            }
        };
        var stdioCommand = new Command("stdio", "Proxy stdin/stdout/stderr of local executables")
        {
            stdioConfigFileOption,
            // Add a catch-all argument to consume remaining args (command to execute)
            new Argument<string[]>("command") { Arity = ArgumentArity.ZeroOrMore }
        };
        Add(stdioCommand);
    }

    public void ParseOptions(string[] args)
    {
        _parseResult = Parse(args);
    }
}