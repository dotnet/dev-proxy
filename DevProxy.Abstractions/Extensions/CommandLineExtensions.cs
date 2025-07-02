// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

#pragma warning disable IDE0130
namespace System.CommandLine.Parsing;
#pragma warning restore IDE0130

public static class CommandLineExtensions
{
    public static T? GetValue<T>(this ParseResult parseResult, string optionName, IList<Option> options)
    {
        ArgumentNullException.ThrowIfNull(parseResult);

        // we need to remove the leading - because CommandLine stores the option
        // name without them
        if (options
            .FirstOrDefault(o => o.Name == optionName.TrimStart('-')) is not Option<T> option)
        {
            throw new InvalidOperationException($"Could not find option with name {optionName} and value type {typeof(T).Name}");
        }

        return parseResult.GetValue(option);
    }

    public static T? GetValueOrDefault<T>(this ParseResult parseResult, string optionName)
    {
        ArgumentNullException.ThrowIfNull(parseResult);

        try
        {
            return parseResult.GetValue<T>(optionName);
        }
        catch (Exception ex) when (ex is InvalidCastException or ArgumentException or InvalidOperationException)
        {
            return default;
        }
    }

    public static IEnumerable<T> OrderByName<T>(this IEnumerable<T> symbols) where T : Symbol
    {
        ArgumentNullException.ThrowIfNull(symbols);

        return symbols.OrderBy(ByName, StringComparer.Ordinal);
    }

    public static void AddCommands(this Command command, IEnumerable<Command> subcommands)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(subcommands);

        foreach (var subcommand in subcommands)
        {
            command.Add(subcommand);
        }
    }

    public static void AddOptions(this Command command, IEnumerable<Option> options)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(options);

        foreach (var option in options)
        {
            command.Add(option);
        }
    }

    private static string ByName<T>(T symbol) where T : Symbol => symbol.Name;
}
