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
/// CurrentUser root store, Linux system store via <see cref="LinuxCertificateHelper"/> on
/// explicit <c>cert ensure</c> only) gated by the user's install/first-run configuration. The trust
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
                if (MacCertificateHelper.TrustCertificate(rootCertificate, logger))
                {
                    logger.LogInformation("Certificate trusted successfully.");
                }
                break;

            case RootTrustAction.TrustWindowsStore:
                InstallIntoWindowsRootStore(rootCertificate);
                break;

            case RootTrustAction.ManualLinux:
                logger.LogWarning(
                    "To have your tools accept intercepted HTTPS traffic, trust the Dev Proxy root certificate by running 'devproxy cert ensure' (requires sudo).");
                break;

            case RootTrustAction.Skip:
            default:
                break;
        }
    }

    public void Trust(X509Certificate2 rootCertificate)
    {
        ArgumentNullException.ThrowIfNull(rootCertificate);

        if (OperatingSystem.IsMacOS())
        {
            if (!MacCertificateHelper.TrustCertificate(rootCertificate, logger))
            {
                throw new InvalidOperationException("Failed to trust the root certificate in the macOS keychain.");
            }
            logger.LogInformation("Certificate trusted successfully.");
        }
        else if (OperatingSystem.IsWindows())
        {
            if (!InstallIntoWindowsRootStore(rootCertificate))
            {
                throw new InvalidOperationException("Failed to install the root certificate into the Windows root store.");
            }
        }
        else if (OperatingSystem.IsLinux())
        {
            var store = LinuxCertificateHelper.DetectTrustStore();
            if (store is null)
            {
                LinuxCertificateHelper.WriteManualTrustInstructions(rootCertificate, logger);
                return;
            }
            switch (LinuxCertificateHelper.TrustCertificate(rootCertificate, store, logger))
            {
                case LinuxTrustResult.Failed:
                    throw new InvalidOperationException("Failed to trust the root certificate in the system certificate store.");
                case LinuxTrustResult.Trusted:
                    logger.LogInformation("Certificate trusted successfully.");
                    break;
                case LinuxTrustResult.TrustedSystemOnly:
                case LinuxTrustResult.Skipped:
                default:
                    break;
            }
        }
        else
        {
            logger.LogWarning(
                "Trust the Dev Proxy root certificate manually so your tools accept intercepted HTTPS traffic.");
        }
    }

    public void Untrust(X509Certificate2 rootCertificate)
    {
        ArgumentNullException.ThrowIfNull(rootCertificate);

        if (OperatingSystem.IsMacOS())
        {
            MacCertificateHelper.RemoveTrustedCertificate(rootCertificate, logger);
            HasRunFlag.Remove();
        }
        else if (OperatingSystem.IsWindows())
        {
            RemoveFromWindowsRootStore(rootCertificate);
        }
        else if (OperatingSystem.IsLinux())
        {
            if (!LinuxCertificateHelper.RemoveTrustedCertificate(rootCertificate, logger))
            {
                throw new InvalidOperationException("Failed to fully remove the root certificate from the system certificate store and NSS databases.");
            }
        }
        else
        {
            logger.LogWarning(
                "Remove the Dev Proxy root certificate from your trust store manually.");
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

    private bool InstallIntoWindowsRootStore(X509Certificate2 rootCertificate)
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
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
            return true;
        }
        catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or IOException or UnauthorizedAccessException)
        {
            logger.LogError(ex, "Failed to install the root certificate into the Windows root store.");
            return false;
        }
    }

    private void RemoveFromWindowsRootStore(X509Certificate2 rootCertificate)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            using var publicCert = X509CertificateLoader.LoadCertificate(
                rootCertificate.Export(X509ContentType.Cert));
            using var store = new X509Store(StoreName.Root, StoreLocation.CurrentUser);
            store.Open(OpenFlags.ReadWrite);

            if (store.Certificates.Contains(publicCert))
            {
                store.Remove(publicCert);
                logger.LogInformation("Certificate removed from the current user's root store.");
            }
        }
        catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or IOException or UnauthorizedAccessException)
        {
            logger.LogError(ex, "Failed to remove the root certificate from the Windows root store.");
        }
    }
}
