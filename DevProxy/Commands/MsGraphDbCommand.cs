// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Abstractions.Utils;
using System.CommandLine;

namespace DevProxy.Commands;

sealed class MsGraphDbCommand : Command
{
    private readonly ILogger _logger;
    private readonly MSGraphDb _msGraphDb;

    public MsGraphDbCommand(ILogger<MsGraphDbCommand> logger, MSGraphDb msGraphDb) :
        base("msgraphdb", "Generate a local SQLite database with Microsoft Graph API metadata")
    {
        _logger = logger;
        _msGraphDb = msGraphDb;
        ConfigureCommand();
    }

    private void ConfigureCommand()
    {
        SetAction(GenerateMsGraphDbAsync);
    }

    private async Task GenerateMsGraphDbAsync(ParseResult parseResult, CancellationToken cancellationToken)
    {
        _ = await _msGraphDb.GenerateDbAsync(false, cancellationToken);
    }
}