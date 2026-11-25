// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Commands;
using Xunit;

namespace DevProxy.Tests;

public class HelpExamplesTests
{
    [Fact]
    public void Format_AlignsDescriptionsAtSharedColumn()
    {
        var lines = HelpExamples.Format([
            ("devproxy", "Start with default config"),
            ("devproxy -u \"https://api.example.com/*\"", "Watch specific URLs"),
        ]).ToArray();

        Assert.Equal(HelpExamples.MinDescriptionColumn, lines[0].IndexOf("Start", StringComparison.Ordinal));
        Assert.Equal(HelpExamples.MinDescriptionColumn, lines[1].IndexOf("Watch", StringComparison.Ordinal));
    }

    [Fact]
    public void Format_WidensColumnForLongCommands()
    {
        var longCommand = new string('x', HelpExamples.MinDescriptionColumn);

        var lines = HelpExamples.Format([
            ("devproxy", "Short"),
            (longCommand, "Long"),
        ]).ToArray();

        Assert.Equal(longCommand.Length + 2, lines[0].IndexOf("Short", StringComparison.Ordinal));
        Assert.Equal(longCommand.Length + 2, lines[1].IndexOf("Long", StringComparison.Ordinal));
    }
}
