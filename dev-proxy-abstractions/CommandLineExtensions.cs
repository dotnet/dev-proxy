// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.CommandLine;
using System.CommandLine.Parsing;

namespace DevProxy.Abstractions;

public static class CommandLineExtensions
{
    public static T? GetValueForOption<T>(this ParseResult parseResult, string optionName, Option[] options)
    {
        // we need to remove the leading - because CommandLine stores the option
        // name without them
        if (options
            .FirstOrDefault(o => o.Name == optionName.TrimStart('-')) is not Option<T> option)
        {
            throw new InvalidOperationException($"Could not find option with name {optionName} and value type {typeof(T).Name}");
        }

        return parseResult.GetValueForOption(option);
    }

    private static string ByName<T>(T symbol) where T : Symbol => symbol.Name;

    public static IEnumerable<T> OrderByName<T>(this IEnumerable<T> symbols) where T : Symbol
    {
        ArgumentNullException.ThrowIfNull(symbols);

        return symbols.OrderBy(ByName, StringComparer.Ordinal);
    }

    public static Command AddCommands(this Command command, IEnumerable<Command> subcommands)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(subcommands);

        foreach (var subcommand in subcommands)
        {
            command.AddCommand(subcommand);
        }
        return command;
    }

    public static Command AddOptions(this Command command, IEnumerable<Option> options)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(options);

        foreach (var option in options)
        {
            command.AddOption(option);
        }
        return command;
    }
}
