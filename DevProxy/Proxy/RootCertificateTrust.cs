// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Security.Cryptography.X509Certificates;
using DevProxy.Abstractions.Proxy;

namespace DevProxy.Proxy;

/// <summary>
/// Host-side <see cref="IRootCertificateTrust"/> for the Kestrel engine. The engine mints
/// and persists its root, then calls <see cref="EnsureTrusted"/>; this performs the actual
/// platform trust install (macOS keychain via <see cref="MacCertificateHelper"/>, Windows
/// CurrentUser root store) gated by the user's install/first-run configuration. The trust
/// decision itself lives in <see cref="RootTrustPolicy"/> (pure + unit-tested); this class
/// is only the I/O around it.
/// </summary>
internal sealed class RootCertificateTrust(
    IProxyConfiguration configuration,
    ILogger<RootCertificateTrust> logger) : IRootCertificateTrust
{
    public void EnsureTrusted(X509Certificate2 rootCertificate)
    {
        ArgumentNullException.ThrowIfNull(rootCertificate);

        var isMac = OperatingSystem.IsMacOS();
        var isWindows = OperatingSystem.IsWindows();

        // Only consult the first-run flag / prompt the user when we're actually on the
        // macOS first-run path, so non-mac platforms have no side effects.
        var isFirstRun = false;
        string? answer = null;
        if (isMac && configuration.InstallCert && !configuration.NoFirstRun)
        {
            isFirstRun = HasRunFlag.CreateIfMissing();
            if (isFirstRun)
            {
                answer = PromptForTrust();
            }
        }

        var action = RootTrustPolicy.Decide(
            isMac,
            isWindows,
            configuration.InstallCert,
            configuration.NoFirstRun,
            isFirstRun,
            answer);

        switch (action)
        {
            case RootTrustAction.TrustMacKeychain:
                MacCertificateHelper.TrustCertificate(rootCertificate, logger);
                logger.LogInformation("Certificate trusted successfully.");
                break;

            case RootTrustAction.TrustWindowsStore:
                InstallIntoWindowsRootStore(rootCertificate);
                break;

            case RootTrustAction.ManualLinux:
                logger.LogWarning(
                    "Trust the Dev Proxy root certificate manually so your tools accept intercepted HTTPS traffic.");
                break;

            case RootTrustAction.Skip:
            default:
                break;
        }
    }

    private static string? PromptForTrust()
    {
        Console.WriteLine();
        Console.WriteLine("Dev Proxy uses a self-signed certificate to intercept and inspect HTTPS traffic.");

        if (Console.IsInputRedirected || Environment.GetEnvironmentVariable("CI") is not null)
        {
            // Non-interactive (CI / piped stdin): default to trusting.
            return "y";
        }

        Console.Write("Update the certificate in your Keychain so that it's trusted by your browser? (Y/n): ");
        return Console.ReadLine()?.Trim();
    }

    private void InstallIntoWindowsRootStore(X509Certificate2 rootCertificate)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            // Install the public certificate only — the private key never belongs in a
            // trust store. Idempotent: skip if an identical cert is already present.
            using var publicCert = X509CertificateLoader.LoadCertificate(
                rootCertificate.Export(X509ContentType.Cert));
            using var store = new X509Store(StoreName.Root, StoreLocation.CurrentUser);
            store.Open(OpenFlags.ReadWrite);

            if (!store.Certificates.Contains(publicCert))
            {
#pragma warning disable CA5380 // Installing the Dev Proxy root is the explicit, user-consented purpose of the proxy.
                store.Add(publicCert);
#pragma warning restore CA5380
                logger.LogInformation("Certificate installed into the current user's root store.");
            }
        }
        catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or IOException or UnauthorizedAccessException)
        {
            logger.LogError(ex, "Failed to install the root certificate into the Windows root store.");
        }
    }
}
