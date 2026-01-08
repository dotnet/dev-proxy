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
        Arity = ArgumentArity.OneOrMore
    };

    public StdioCommand(
        ILogger<StdioCommand> logger,
        ILoggerFactory loggerFactory,
        IEnumerable<IStdioPlugin> plugins) :
        base("stdio", "Proxy stdin/stdout/stderr of local executables")
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

        SetAction(RunAsync);
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
