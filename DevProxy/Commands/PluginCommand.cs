// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Abstractions.Plugins;
using DevProxy.Abstractions.Proxy;
using DevProxy.Abstractions.Utils;
using DevProxy.Logging;
using DevProxy.Plugins;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.CommandLine;
using System.CommandLine.Invocation;
using System.CommandLine.Parsing;
using System.Reflection;
using System.Text.Json;

namespace DevProxy.Commands;

sealed class PluginInfo
{
    public required string Name { get; init; }
    public required string PluginPath { get; init; }
    public required bool IsConfigurable { get; init; }
}

sealed class PluginCommand : Command
{
    internal const string DefaultPluginPath = "~appFolder/plugins/DevProxy.Plugins.dll";

    public PluginCommand(ILogger<PluginCommand> logger) : base("plugin", "Manage Dev Proxy plugins")
    {
        var listCommand = new Command("list", "List plugins available in a plugin assembly");
        var pluginPathOption = new Option<string?>("--plugin-path")
        {
            Description = $"Path to the plugin assembly to inspect. Defaults to {DefaultPluginPath}",
            HelpName = "path"
        };
        listCommand.Add(pluginPathOption);
        HelpExamples.Add(listCommand, [
            ("devproxy plugin list", "List built-in plugins"),
            ("devproxy plugin list --output json", "List built-in plugins as JSON"),
            ("devproxy plugin list --plugin-path ./MyPlugins.dll", "List plugins in a custom assembly"),
        ]);
        listCommand.SetAction(parseResult =>
        {
            var pluginPath = parseResult.GetValue(pluginPathOption);
            var outputFormat = parseResult.GetValueOrDefault<OutputFormat?>(DevProxyCommand.OutputOptionName) ?? OutputFormat.Text;
            return ListPlugins(pluginPath, outputFormat, logger);
        });

        Add(listCommand);
    }

    /// <summary>
    /// Runs the plugin command standalone, without requiring the full DI
    /// container, so that plugins can be listed even when there's no valid
    /// Dev Proxy configuration file available.
    /// </summary>
    internal static async Task<int> RunStandaloneAsync(string[] args)
    {
        var options = new DevProxyConfigOptions();
        options.ParseOptions(args);
        using var loggerFactory = LoggerFactory.Create(builder =>
        {
            _ = builder
                .SetMinimumLevel(options.LogLevel ?? LogLevel.Information)
                .AddConsole(consoleOptions =>
                {
                    consoleOptions.FormatterName = options.Output == OutputFormat.Json
                        ? JsonConsoleFormatter.FormatterName
                        : ProxyConsoleFormatter.DefaultCategoryName;
                    consoleOptions.LogToStandardErrorThreshold = LogLevel.Warning;
                })
                .AddConsoleFormatter<ProxyConsoleFormatter, ProxyConsoleFormatterOptions>(formatterOptions =>
                {
                    formatterOptions.ShowTimestamps = false;
                })
                .AddConsoleFormatter<JsonConsoleFormatter, ProxyConsoleFormatterOptions>(formatterOptions =>
                {
                    formatterOptions.ShowTimestamps = false;
                });
        });

        var outputOption = new Option<OutputFormat?>(DevProxyCommand.OutputOptionName)
        {
            Description = $"Output format. Allowed values: {string.Join(", ", Enum.GetNames<OutputFormat>())}",
            HelpName = "format",
            Recursive = true
        };
        var noColorOption = new Option<bool>(DevProxyCommand.NoColorOptionName)
        {
            Description = "Disable colored output",
            Arity = ArgumentArity.Zero,
            Recursive = true
        };
        var logLevelOption = new Option<LogLevel?>(DevProxyCommand.LogLevelOptionName)
        {
            Description = $"Level of messages to log. Allowed values: {string.Join(", ", Enum.GetNames<LogLevel>())}",
            HelpName = "log-level",
            Recursive = true
        };
        var rootCommand = new RootCommand("Dev Proxy")
        {
            outputOption,
            noColorOption,
            logLevelOption,
            new PluginCommand(loggerFactory.CreateLogger<PluginCommand>())
        };
        HelpExamples.Install(rootCommand);

        var parseResult = rootCommand.Parse(args);
        if (parseResult.Action is ParseErrorAction parseErrorAction)
        {
            if (DevProxyCommand.IsJsonOutputRequested(parseResult))
            {
                DevProxyCommand.WriteJsonParseErrors(parseResult);
                return 2;
            }

            parseErrorAction.ShowHelp = false;
        }

        var exitCode = await parseResult.InvokeAsync();
        return exitCode != 0 && parseResult.Errors.Count > 0 ? 2 : exitCode;
    }

    internal static int ListPlugins(string? pluginPath, OutputFormat outputFormat, ILogger logger)
    {
        var configuredPath = string.IsNullOrWhiteSpace(pluginPath) ? DefaultPluginPath : pluginPath;
        var expandedPath = ProxyUtils.ReplacePathTokens(configuredPath.Replace('\\', Path.DirectorySeparatorChar));
        var fullPath = Path.GetFullPath(expandedPath);
        // config resolves relative plugin paths against the config file, not
        // the current directory, so report relative paths as absolute
        if (!Path.IsPathRooted(expandedPath))
        {
            configuredPath = fullPath;
        }

        IReadOnlyList<PluginInfo> plugins;
        try
        {
            plugins = GetPlugins(fullPath, configuredPath);
        }
        catch (Exception ex) when (ex is FileNotFoundException or BadImageFormatException or FileLoadException)
        {
            logger.LogError("Could not load plugin assembly '{AssemblyPath}': {Message}", fullPath, ex.Message);
            return 1;
        }
        catch (ReflectionTypeLoadException ex)
        {
            logger.LogError("Could not load types from plugin assembly '{AssemblyPath}':", fullPath);
            foreach (var message in ex.LoaderExceptions.Select(e => e?.Message).Where(m => m is not null).Distinct())
            {
                logger.LogError("  {Message}", message);
            }
            return 1;
        }

        WritePlugins(plugins, fullPath, configuredPath, outputFormat, logger);
        return 0;
    }

    internal static void WritePlugins(IReadOnlyList<PluginInfo> plugins, string assemblyPath, string pluginPath, OutputFormat outputFormat, ILogger logger)
    {
        if (outputFormat == OutputFormat.Json)
        {
            logger.LogStructuredOutput(JsonSerializer.Serialize(plugins, ProxyUtils.JsonSerializerOptions));
            return;
        }

        if (plugins.Count == 0)
        {
            logger.LogInformation("No plugins found in {AssemblyPath}", assemblyPath);
            return;
        }

        logger.LogInformation("Plugins in {AssemblyPath}:", assemblyPath);
        logger.LogInformation("");
        foreach (var plugin in plugins)
        {
            logger.LogInformation("  {Name}{ConfigurationHint}", plugin.Name, plugin.IsConfigurable ? " (configurable)" : "");
        }
        logger.LogInformation("");
        logger.LogInformation("To use a plugin, add it to the plugins array in your config with \"name\": \"<plugin name>\" and \"pluginPath\": {PluginPath}.",
            JsonSerializer.Serialize(pluginPath, ProxyUtils.JsonSerializerOptions));
    }

    internal static IReadOnlyList<PluginInfo> GetPlugins(string assemblyPath, string pluginPath)
    {
        if (!File.Exists(assemblyPath))
        {
            throw new FileNotFoundException("File not found", assemblyPath);
        }

        var loadContext = new PluginLoadContext(assemblyPath);
        var assembly = loadContext.LoadFromAssemblyName(new AssemblyName(Path.GetFileNameWithoutExtension(assemblyPath)));
        return GetPlugins(assembly, pluginPath);
    }

    internal static IReadOnlyList<PluginInfo> GetPlugins(Assembly assembly, string pluginPath)
    {
        // like the runtime loader, fail if any type can't be loaded
        return [.. assembly.GetTypes()
            .Where(t => t.IsClass && t.IsVisible && !t.IsAbstract && !t.ContainsGenericParameters && typeof(IPlugin).IsAssignableFrom(t))
            .OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
            .Select(t => new PluginInfo
            {
                Name = t.Name,
                PluginPath = pluginPath,
                IsConfigurable = PluginServiceExtensions.IsConfigurablePlugin(t, out _)
            })];
    }
}
