// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

namespace DevProxy.CommandHandlers;

public static class CertEnsureCommandHandler
{
    public static async Task EnsureCertAsync(ILogger logger)
    {
        logger.LogTrace("EnsureCertAsync() called");

        try
        {
            logger.LogInformation("Ensuring certificate exists and is trusted...");
            await ProxyEngine.ProxyServer.CertificateManager.EnsureRootCertificateAsync();
            logger.LogInformation("DONE");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error ensuring certificate");
        }

        logger.LogTrace("EnsureCertAsync() finished");
    }
}
