// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

namespace DevProxy.CommandHandlers;

public static class CertRemoveCommandHandler
{
    public static void RemoveCert(ILogger logger)
    {
        logger.LogTrace("RemoveCert() called");

        try
        {
            logger.LogInformation("Uninstalling the root certificate...");
            ProxyEngine.ProxyServer.CertificateManager.RemoveTrustedRootCertificate(machineTrusted: false);
            logger.LogInformation("DONE");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error removing certificate");
        }

        logger.LogTrace("RemoveCert() finished");
    }
}
