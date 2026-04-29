// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.CommandLine;
using System.CommandLine.Help;
using System.CommandLine.Invocation;

namespace DevProxy.Commands;

sealed class ExitCodeHelpAction : SynchronousCommandLineAction
{
    private readonly HelpAction _originalAction;

    public override bool Terminating => true;

    public ExitCodeHelpAction(HelpAction originalAction)
    {
        _originalAction = originalAction;
    }

    public override int Invoke(ParseResult parseResult)
    {
        var result = _originalAction.Invoke(parseResult);

        var output = Console.Out;
        output.WriteLine();
        output.WriteLine("Exit codes:");
        output.WriteLine("  0  Success");
        output.WriteLine("  1  Runtime error");
        output.WriteLine("  2  Invalid input or usage");

        return result;
    }
}
