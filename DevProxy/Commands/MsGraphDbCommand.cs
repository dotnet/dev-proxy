// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Abstractions.Data;
using System.CommandLine;

namespace DevProxy.Commands;

sealed class MsGraphDbCommand : Command
{
    private readonly MSGraphDb _msGraphDb;

    public MsGraphDbCommand(MSGraphDb msGraphDb) :
        base("msgraphdb", "Generate a local SQLite database with Microsoft Graph API metadata")
    {
        _msGraphDb = msGraphDb;
        ConfigureCommand();
    }

    private void ConfigureCommand()
    {
        HelpExamples.Add(this, [
            "devproxy msgraphdb                                  Generate Microsoft Graph DB",
        ]);

        SetAction(GenerateMsGraphDbAsync);
    }

    private async Task GenerateMsGraphDbAsync(ParseResult parseResult, CancellationToken cancellationToken)
    {
        _ = await _msGraphDb.GenerateDbAsync(false, cancellationToken);
    }
}