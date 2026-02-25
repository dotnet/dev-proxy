// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.CommandLine;
using System.CommandLine.Help;
using System.CommandLine.Invocation;

namespace DevProxy.Commands;

sealed class DevProxyHelpAction(HelpAction originalAction) : SynchronousCommandLineAction
{
    public override int Invoke(ParseResult parseResult)
    {
        originalAction.Invoke(parseResult);

        if (parseResult.CommandResult.Command is not RootCommand)
        {
            return 0;
        }

        var output = parseResult.Configuration.Output;

        output.WriteLine();
        output.WriteLine("  Additional commands may be available depending on configured plugins.");
        output.WriteLine("  Use -c <config-file> to load a specific configuration.");
        output.WriteLine();
        output.WriteLine("Output:");
        output.WriteLine("  Primary output goes to stdout. Errors and diagnostics go to stderr.");
        output.WriteLine("  Use --log-for Machine for structured output.");
        output.WriteLine();
        output.WriteLine("Configuration precedence:");
        output.WriteLine("  CLI flags > config file > defaults");

        return 0;
    }
}
