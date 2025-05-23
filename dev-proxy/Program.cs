﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy;
using DevProxy.Abstractions;
using DevProxy.Abstractions.LanguageModel;
using DevProxy.CommandHandlers;
using DevProxy.Logging;
using System.CommandLine;

_ = Announcement.ShowAsync();

PluginEvents pluginEvents = new();

(ILogger, ILoggerFactory) BuildLogger()
{
    var loggerFactory = LoggerFactory.Create(builder =>
    {
        builder
            .AddConsole(options =>
            {
                options.FormatterName = ProxyConsoleFormatter.DefaultCategoryName;
                options.LogToStandardErrorThreshold = LogLevel.Warning;
            })
            .AddConsoleFormatter<ProxyConsoleFormatter, ProxyConsoleFormatterOptions>(options =>
            {
                options.IncludeScopes = true;
                options.ShowSkipMessages = ProxyCommandHandler.Configuration.ShowSkipMessages;
                options.ShowTimestamps = ProxyCommandHandler.Configuration.ShowTimestamps;
            })
            .AddRequestLogger(pluginEvents)
            .SetMinimumLevel(ProxyHost.LogLevel ?? ProxyCommandHandler.Configuration.LogLevel);
    });
    return (loggerFactory.CreateLogger(ProxyConsoleFormatter.DefaultCategoryName), loggerFactory);
}

var (logger, loggerFactory) = BuildLogger();

var lmClient = LanguageModelClientFactory.Create(ProxyCommandHandler.Configuration.LanguageModel, logger);
IProxyContext context = new ProxyContext(ProxyCommandHandler.Configuration, ProxyEngine.Certificate, lmClient);
ProxyHost proxyHost = new();

// store the global options that are created automatically for us
// rootCommand doesn't return the global options, so we have to store them manually
string[] globalOptions = ["--version"];
string[] helpOptions = ["--help", "-h", "/h", "-?", "/?"];

// check if any of the global- or help options are present
var hasGlobalOption = args.Any(arg => globalOptions.Contains(arg));
var hasHelpOption = args.Any(arg => helpOptions.Contains(arg));

var isDiscover = args.Contains("--discover", StringComparer.OrdinalIgnoreCase);
if (isDiscover)
{
    logger.LogWarning("Dev Proxy is running in URL discovery mode. Configured plugins and URLs to watch will be ignored.");
}

// load plugins to get their options and commands
var pluginLoader = new PluginLoader(isDiscover, logger, loggerFactory);
PluginLoaderResult loaderResults = await pluginLoader.LoadPluginsAsync(pluginEvents, context);

var pluginOptions = loaderResults.ProxyPlugins
    .SelectMany(p => p.GetOptions())
    // remove duplicates by comparing the option names
    .GroupBy(o => o.Name)
    .Select(g => g.First())
    .ToArray();

var pluginCommands = loaderResults.ProxyPlugins
    .SelectMany(p => p.GetCommands())
    .ToArray();

// this is where the root command is created which contains all commands and subcommands
RootCommand rootCommand = proxyHost.CreateRootCommand(logger, pluginOptions, pluginCommands);

// get the list of available subcommand's names
var subCommands = rootCommand.Children.OfType<Command>().Select(c => c.Name).ToArray();

// check if any of the subcommands are present
var hasSubCommand = args.Any(arg => subCommands.Contains(arg));

if (hasGlobalOption || hasSubCommand)
{
    // we don't need to init plugins if the user is using a global option or
    // using a subcommand, so we can exit early
    var exitCode = await rootCommand.InvokeAsync(args);
    // required to output all messages before closing the program
    loggerFactory.Dispose();
    Environment.Exit(exitCode);
    return;
}

// filter args to retrieve options
var incomingOptions = args.Where(arg => arg.StartsWith('-')).ToArray();

// remove the global- and help options from the incoming options
incomingOptions = [.. incomingOptions.Except([.. globalOptions, .. helpOptions])];

// compare the incoming options against the root command options
foreach (var option in rootCommand.Options)
{
    // get the option aliases
    var aliases = option.Aliases.ToArray();

    // iterate over aliases
    foreach (string alias in aliases)
    {
        // if the alias is present
        if (incomingOptions.Contains(alias))
        {
            // remove the option from the incoming options
            incomingOptions = incomingOptions.Where(val => val != alias).ToArray();
        }
    }
}

// list the remaining incoming options as unknown in the output
if (incomingOptions.Length > 0)
{
    Console.Error.WriteLine("Unknown option(s): {0}", string.Join(" ", incomingOptions));
    Console.Error.WriteLine("TIP: Use --help view available options");
    Console.Error.WriteLine("TIP: Are you missing a plugin? See: https://aka.ms/devproxy/plugins");
    Environment.Exit(1);
}
else
{
    if (!hasHelpOption)
    {
        // have all the plugins init
        pluginEvents.RaiseInit(new InitArgs());
    }

    rootCommand.Handler = proxyHost.GetCommandHandler(pluginEvents, [.. pluginOptions], loaderResults.UrlsToWatch, logger);
    var exitCode = await rootCommand.InvokeAsync(args);
    loggerFactory.Dispose();
    Environment.Exit(exitCode);
}
