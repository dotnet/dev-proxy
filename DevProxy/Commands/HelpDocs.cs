// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Abstractions.Utils;
using System.CommandLine;
using System.CommandLine.Parsing;
using System.Runtime.CompilerServices;

namespace DevProxy.Commands;

static class HelpDocs
{
    // Commands with an explicit docs link (for example contributed by plugins).
    // A null value means the command has no canonical docs page.
    private static readonly ConditionalWeakTable<Command, StrongBox<string?>> _links = new();

    public static void Set(Command command, string? link, bool recursive = true)
    {
        _links.AddOrUpdate(command, new(link));
        if (!recursive)
        {
            return;
        }

        foreach (var subcommand in command.Subcommands)
        {
            Set(subcommand, link, recursive);
        }
    }

    public static string? GetLink(ParseResult parseResult)
    {
        var command = parseResult.CommandResult.Command;
        if (_links.TryGetValue(command, out var link))
        {
            return link.Value;
        }

        if (command is RootCommand || command.Action is null)
        {
            return DocsLinks.Cli;
        }

        var path = new List<string>();
        for (var result = parseResult.CommandResult; result is not null; result = result.Parent as CommandResult)
        {
            if (result.Command is RootCommand)
            {
                break;
            }
            path.Insert(0, result.Command.Name);
        }

        return DocsLinks.Get($"cli-{string.Join('-', path)}");
    }
}
