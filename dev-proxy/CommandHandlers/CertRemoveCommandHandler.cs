// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.CommandLine;
using System.CommandLine.Invocation;

namespace DevProxy.CommandHandlers;

public static class CertRemoveCommandHandler
{
    public static void RemoveCert(ILogger logger, InvocationContext invocationContext, Option<bool> forceOption)
    {
        logger.LogTrace("RemoveCert() called");
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(invocationContext);
        ArgumentNullException.ThrowIfNull(forceOption);

        try
        {
            var isForced = invocationContext.ParseResult.GetValueForOption(forceOption);
            if (!isForced)
            {
                var isConfirmed = PromptConfirmation("Do you want to remove the root certificate", defaultValue: false);
                if (!isConfirmed)
                {
                    return;
                }
            }

            logger.LogInformation("Uninstalling the root certificate...");

            ProxyEngine.ProxyServer.CertificateManager.RemoveTrustedRootCertificate(machineTrusted: false);

            logger.LogInformation("DONE");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error removing certificate");
        }
        finally
        {
            logger.LogTrace("RemoveCert() finished");
        }
    }

    private static bool PromptConfirmation(string message, bool defaultValue)
    {
        while (true)
        {
            Console.Write(message + $" ({(defaultValue ? "Y/n" : "y/N")}): ");
            var answer = Console.ReadLine();

            if (string.IsNullOrWhiteSpace(answer))
            {
                return defaultValue;
            }
            else if (answer.StartsWith("y", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            else if (answer.StartsWith("n", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }
    }
}
