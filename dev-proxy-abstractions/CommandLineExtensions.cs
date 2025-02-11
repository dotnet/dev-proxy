// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.CommandLine;
using System.CommandLine.Parsing;

namespace Microsoft.DevProxy.Abstractions;

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
}
