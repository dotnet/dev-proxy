// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.CommandLine;
using System.CommandLine.Help;
using System.CommandLine.Invocation;

namespace DevProxy.Commands;

sealed class DevProxyHelpAction(SynchronousCommandLineAction originalAction) : SynchronousCommandLineAction
{
    public override int Invoke(ParseResult parseResult)
    {
        originalAction.Invoke(parseResult);

        var output = Console.Out;

        if (parseResult.CommandResult.Command is RootCommand)
        {
            WriteRootHelp(output);
        }

        var docsLink = HelpDocs.GetLink(parseResult);
        if (docsLink is not null)
        {
            output.WriteLine($"Docs: {docsLink}");
        }

        return 0;
    }

    private static void WriteRootHelp(TextWriter output)
    {
        output.WriteLine();
        output.WriteLine("  Additional commands may be available depending on configured plugins.");
        output.WriteLine("  Use -c <config-file> to load a specific configuration.");
        output.WriteLine();
        output.WriteLine("Output:");
        output.WriteLine("  Primary output goes to stdout. Errors and diagnostics go to stderr.");
        output.WriteLine("  Use --output json for structured output.");
        output.WriteLine();
        output.WriteLine("Configuration precedence:");
        output.WriteLine("  CLI flags > config file > defaults");
        output.WriteLine();
        output.WriteLine("Exit codes:");
        output.WriteLine("  0  Success");
        output.WriteLine("  1  Runtime error");
        output.WriteLine("  2  Invalid input or usage");
        output.WriteLine();
    }
}
