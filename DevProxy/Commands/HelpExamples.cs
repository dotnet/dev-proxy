// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.CommandLine;
using System.CommandLine.Help;
using System.CommandLine.Invocation;
using System.Runtime.CompilerServices;

namespace DevProxy.Commands;

static class HelpExamples
{
    private static readonly ConditionalWeakTable<Command, string[]> _examples = new();

    public static void Add(Command command, string[] examples)
    {
        _examples.AddOrUpdate(command, examples);
    }

    public static void Install(RootCommand rootCommand)
    {
        var helpOption = rootCommand.Options.OfType<HelpOption>().First();
        helpOption.Action = new ExamplesHelpAction();
    }

    private sealed class ExamplesHelpAction : SynchronousCommandLineAction
    {
        public override int Invoke(ParseResult parseResult)
        {
            new HelpAction().Invoke(parseResult);

            var command = parseResult.CommandResult.Command;
            if (_examples.TryGetValue(command, out var examples))
            {
                var output = parseResult.Configuration.Output;
                output.WriteLine("Examples:");
                foreach (var example in examples)
                {
                    output.Write("  ");
                    output.WriteLine(example);
                }
                output.WriteLine();
            }
            return 0;
        }
    }
}
