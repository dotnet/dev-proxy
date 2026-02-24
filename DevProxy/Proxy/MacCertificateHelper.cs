// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Diagnostics;
using System.Security.Cryptography.X509Certificates;

namespace DevProxy.Proxy;

internal static class MacCertificateHelper
{
    internal static void TrustCertificate(X509Certificate2 certificate, ILogger logger)
    {
        var pemFilePath = ExportCertificateToPem(certificate, logger);
        if (pemFilePath is null)
        {
            return;
        }

        try
        {
            var keychainPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Library", "Keychains", "login.keychain-db");
            RunSecurityCommand(
                $"add-trusted-cert -r trustRoot -k \"{keychainPath}\" \"{pemFilePath}\"",
                "trust",
                logger);
        }
        finally
        {
            CleanupFile(pemFilePath);
        }
    }

    internal static void RemoveTrustedCertificate(X509Certificate2 certificate, ILogger logger)
    {
        var pemFilePath = ExportCertificateToPem(certificate, logger);
        if (pemFilePath is null)
        {
            return;
        }

        try
        {
            RunSecurityCommand(
                $"remove-trusted-cert \"{pemFilePath}\"",
                "remove trust for",
                logger);
        }
        finally
        {
            CleanupFile(pemFilePath);
        }
    }

    private static string? ExportCertificateToPem(X509Certificate2 certificate, ILogger logger)
    {
        try
        {
            var certBytes = certificate.Export(X509ContentType.Cert);
            var base64Cert = Convert.ToBase64String(certBytes, Base64FormattingOptions.InsertLineBreaks);
            var pem = $"-----BEGIN CERTIFICATE-----\n{base64Cert}\n-----END CERTIFICATE-----";

            var configDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "dev-proxy");
            _ = Directory.CreateDirectory(configDir);
            var pemFilePath = Path.Combine(configDir, "dev-proxy-ca.pem");
            File.WriteAllText(pemFilePath, pem);
            return pemFilePath;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to export certificate to PEM");
            return null;
        }
    }

    private static void RunSecurityCommand(string arguments, string action, ILogger logger)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "/usr/bin/security",
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
        };

        using var process = new Process { StartInfo = startInfo };
        _ = process.Start();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        if (process.ExitCode != 0)
        {
            logger.LogError("Failed to {Action} certificate: {Error}", action, stderr);
        }
        else
        {
            logger.LogInformation("Successfully completed {Action} certificate operation.", action);
        }
    }

    private static void CleanupFile(string filePath)
    {
        try
        {
            File.Delete(filePath);
        }
        catch
        {
            // ignore cleanup failures
        }
    }
}
