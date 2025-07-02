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

    public bool Discover => _parseResult?.GetValueOrDefault<bool?>(DevProxyCommand.DiscoverOptionName) ?? false;
    public string? IPAddress => _parseResult?.GetValueOrDefault<string?>(DevProxyCommand.IpAddressOptionName);
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

        var options = new List<Option>
        {
            ipAddressOption,
            configFileOption,
            urlsToWatchOption,
            logLevelOption
        };
        this.AddOptions(options.OrderByName());
    }

    public void ParseOptions(string[] args)
    {
        _parseResult = Parse(args);
    }
}