// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Abstractions.Plugins;
using DevProxy.Abstractions.Proxy;
using DevProxy.Stdio;
using System.CommandLine;
using System.CommandLine.Parsing;

namespace DevProxy.Commands;

sealed class StdioCommand : Command
{
    private readonly ILogger _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly IEnumerable<IStdioPlugin> _plugins;

    // Global data shared across stdio sessions
    private static readonly Dictionary<string, object> _globalData = [];

    private readonly Argument<string[]> _commandArgument = new("command")
    {
        Description = "The command and arguments to execute",
        Arity = ArgumentArity.OneOrMore,
        // Disable response file expansion (@file) to avoid issues with npm package names like @devproxy/mcp
        // This ensures arguments like "npx @devproxy/mcp" are passed through literally
        HelpName = "command"
    };

    public StdioCommand(
        ILogger<StdioCommand> logger,
        ILoggerFactory loggerFactory,
        IEnumerable<IStdioPlugin> plugins) :
        base("stdio", """
            Proxy stdin/stdout/stderr of local executables.
            Dev Proxy intercepts and processes the stdio streams of the specified command,
            applying configured plugins (mocking, error simulation, etc.) to the traffic.
            Logs are written to a timestamped file (devproxy-stdio-YYYYMMDD-HHmmss.log)
            to avoid interfering with the proxied streams.
            Usage errors and exceptions are written to stderr.

            Examples:
              devproxy stdio npx -y @devproxy/mcp          Proxy MCP server
              devproxy stdio node server.js                Proxy Node.js app
              devproxy stdio -c myconfig.json node app.js  With custom config
            """)
    {
        _logger = logger;
        _loggerFactory = loggerFactory;
        _plugins = plugins;

        ConfigureCommand();
    }

    private void ConfigureCommand()
    {
        Add(_commandArgument);

        var options = new List<Option> { DevProxyCommand.ConfigFileOption };
        // Add plugin options
        options.AddRange(_plugins
            .OfType<IPlugin>()
            .SelectMany(p => p.GetOptions())
            // remove duplicates by comparing the option names
            .GroupBy(o => o.Name)
            .Select(g => g.First()));
        this.AddOptions(options.OrderByName());

        HelpExamples.Add(this, [
            "devproxy stdio npx -y @devproxy/mcp                 Proxy MCP server stdio",
            "devproxy stdio node server.js                       Proxy Node.js app stdio",
            "devproxy stdio -c myconfig.json node app.js         With custom config",
        ]);

        SetAction(RunAsync);
    }

    /// <summary>
    /// Parses arguments for the stdio command with response file expansion disabled.
    /// This allows npm package names like @devproxy/mcp to be passed through literally.
    /// </summary>
    internal static ParseResult ParseStdioArgs(Command rootCommand, string[] args)
    {
        var configuration = new CommandLineConfiguration(rootCommand)
        {
            ResponseFileTokenReplacer = null
        };
        return rootCommand.Parse(args, configuration);
    }

    private async Task<int> RunAsync(ParseResult parseResult, CancellationToken cancellationToken)
    {
        _logger.LogTrace("StdioCommand.RunAsync() called");

        var command = parseResult.GetValue(_commandArgument);

        if (command == null || command.Length == 0)
        {
            _logger.LogError("No command specified");
            await Console.Error.WriteLineAsync("Usage: devproxy stdio <command> [args...]");
            await Console.Error.WriteLineAsync("Example: devproxy stdio npx -y @modelcontextprotocol/server-filesystem");
            return 1;
        }

        _logger.LogInformation("Logging to: {LogFile}", DevProxyCommand.StdioLogFilePath);
        _logger.LogInformation("Starting stdio proxy for command: {Command}", string.Join(" ", command));

        // Notify plugins that options have been loaded (triggers mocks file loading, etc.)
        var optionsLoadedArgs = new OptionsLoadedArgs(parseResult);
        foreach (var plugin in _plugins.Where(p => p.Enabled).OfType<IPlugin>())
        {
            plugin.OptionsLoaded(optionsLoadedArgs);
        }

        var enabledPlugins = _plugins.Where(p => p.Enabled).ToList();
        _logger.LogDebug("Loaded {Count} stdio plugins: {Plugins}",
            enabledPlugins.Count,
            string.Join(", ", enabledPlugins.Select(p => p.Name)));

        var sessionLogger = _loggerFactory.CreateLogger<ProxySession>();

        using var session = new ProxySession(command, enabledPlugins, _globalData, sessionLogger);

        try
        {
            return await session.RunAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error running stdio proxy");
            await Console.Error.WriteLineAsync($"Error: {ex.Message}");
            return 1;
        }
        finally
        {
            _logger.LogTrace("StdioCommand.RunAsync() finished");
        }
    }
}
