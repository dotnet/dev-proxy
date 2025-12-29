// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Stdio;
using System.CommandLine;
using System.CommandLine.Parsing;

namespace DevProxy.Commands;

sealed class StdioCommand : Command
{
    private readonly ILogger _logger;
    private readonly ILoggerFactory _loggerFactory;

    private readonly Option<string?> _logFileOption = new("--log-file", "-l")
    {
        Description = "Path to log file for recording all traffic"
    };

    private readonly Argument<string[]> _commandArgument = new("command")
    {
        Description = "The command and arguments to execute",
        Arity = ArgumentArity.OneOrMore
    };

    public StdioCommand(ILogger<StdioCommand> logger, ILoggerFactory loggerFactory) :
        base("stdio", "Proxy stdin/stdout/stderr of local executables")
    {
        _logger = logger;
        _loggerFactory = loggerFactory;

        ConfigureCommand();
    }

    private void ConfigureCommand()
    {
        this.AddOptions(new List<Option> { _logFileOption }.OrderByName());
        Add(_commandArgument);

        SetAction(RunAsync);
    }

    private async Task<int> RunAsync(ParseResult parseResult, CancellationToken cancellationToken)
    {
        _logger.LogTrace("StdioCommand.RunAsync() called");

        var command = parseResult.GetValue(_commandArgument);
        var logFile = parseResult.GetValue(_logFileOption);

        if (command == null || command.Length == 0)
        {
            _logger.LogError("No command specified");
            await Console.Error.WriteLineAsync("Usage: devproxy stdio <command> [args...]");
            await Console.Error.WriteLineAsync("Example: devproxy stdio npx -y @modelcontextprotocol/server-filesystem");
            return 1;
        }

        _logger.LogDebug("Starting stdio proxy for command: {Command}", string.Join(" ", command));

        if (!string.IsNullOrEmpty(logFile))
        {
            _logger.LogDebug("Logging traffic to: {LogFile}", logFile);
        }

        var sessionLogger = _loggerFactory.CreateLogger<ProxySession>();

        using var session = new ProxySession(command, logFile, sessionLogger);

        // Configure handlers for intercepting traffic
        // These can be customized by plugins in the future
        session.OnStdinReceived = (message) =>
        {
            // Log the message but don't consume it
            _logger.LogDebug("stdin: {Message}", message.TrimEnd());
            return false; // Pass through to child
        };

        session.OnStdoutReceived = (message) =>
        {
            // Log the message but don't consume it
            _logger.LogDebug("stdout: {Message}", message.TrimEnd());
            return false; // Pass through to parent
        };

        session.OnStderrReceived = (message) =>
        {
            // Log the message but don't consume it
            _logger.LogDebug("stderr: {Message}", message.TrimEnd());
            return false; // Pass through to parent
        };

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
