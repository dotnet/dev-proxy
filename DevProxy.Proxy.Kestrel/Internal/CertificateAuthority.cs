// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DevProxy.Proxy.Kestrel.Internal;

/// <summary>
/// A persistent certificate authority. It loads a self-signed root CA from disk
/// (creating + saving one on first run), then mints and caches a leaf certificate
/// per host on demand so the proxy can terminate TLS and inspect decrypted traffic.
///
/// <para>
/// The on-disk layout intentionally mirrors the Titanium engine's
/// <c>CertificateDiskCache</c>: the root lives at <c>&lt;configDir&gt;/rootCert.pfx</c>
/// (overridable via <c>DEV_PROXY_CERT_PATH</c>) and leaves at
/// <c>&lt;configDir&gt;/crts/&lt;host&gt;.pfx</c>, both PKCS#12 with an empty password.
/// Because the format + path match, this CA will <b>load a root that the Titanium
/// engine already created and the OS already trusts</b> — so existing users get
/// interception without re-trusting. Per migration decision #5 there is no
/// cross-version compatibility contract: an absent/invalid/expired root is simply
/// regenerated (and the stale leaf cache purged).
/// </para>
///
/// <para>
/// OS-trust installation (mac keychain / Windows root store) + the first-run prompt
/// are deliberately NOT handled here — that is Slice 5b, wired in the host where the
/// existing trust helpers live. This slice only makes the root <b>persistent</b>.
/// </para>
///
/// <code>
/// LoadOrCreateRoot(rootPfxPath)
///   ┌─ file exists? ──no──┐
///   │ yes                 │
///   ▼                     ▼
///   try load (empty pw)   create fresh root
///   ├─ ok + isCA          ├─ save to rootPfxPath (best-effort)
///   │  + has key          └─ purge stale crts/ (old root's leaves)
///   │  + not expired ─► use
///   └─ otherwise ───────► create fresh root (same as above)
///
/// GetCertificateForHost(host)  [ConcurrentDictionary.GetOrAdd]
///   ┌─ crts/&lt;host&gt;.pfx exists &amp; valid? ──yes──► load + use
///   └─ no ──► mint leaf signed by root ──► save (best-effort) ──► use
/// </code>
/// </summary>
public sealed class CertificateAuthority : IDisposable
{
    private const string RootCertCommonName = "Dev Proxy CA";
    private const string ConfigFolderName = "dev-proxy";
    private const string RootCertFileName = "rootCert.pfx";
    private const string LeafDirectoryName = "crts";
    private const int LeafValidityDays = 365; // < 397 to avoid Edge ERR_CERT_VALIDITY_TOO_LONG

    private readonly string _rootPfxPath;
    private readonly string _leafDirectory;
    private readonly ILogger _logger;
    private readonly X509Certificate2 _ca;
    private readonly ConcurrentDictionary<string, X509Certificate2> _cache = new(StringComparer.OrdinalIgnoreCase);

    public CertificateAuthority(string rootPfxPath, string leafDirectory, ILogger? logger = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(rootPfxPath);
        ArgumentException.ThrowIfNullOrEmpty(leafDirectory);

        _rootPfxPath = rootPfxPath;
        _leafDirectory = leafDirectory;
        _logger = logger ?? NullLogger.Instance;
        _ca = LoadOrCreateRoot();
    }

    /// <summary>
    /// Builds a CA rooted at the real Dev Proxy config directory, mirroring the
    /// Titanium engine's path resolution so an already-trusted root is reused.
    /// </summary>
    public static CertificateAuthority CreateDefault(ILogger? logger = null)
    {
        var configDirectory = ResolveConfigDirectory();

        var envPath = Environment.GetEnvironmentVariable("DEV_PROXY_CERT_PATH");
        var rootPfxPath = !string.IsNullOrEmpty(envPath) && Path.IsPathRooted(envPath)
            ? envPath
            : Path.Combine(configDirectory, RootCertFileName);

        var leafDirectory = Path.Combine(configDirectory, LeafDirectoryName);
        return new CertificateAuthority(rootPfxPath, leafDirectory, logger);
    }

    /// <summary>The root CA certificate clients must trust to allow interception.</summary>
    public X509Certificate2 RootCertificate => _ca;

    public X509Certificate2 GetCertificateForHost(string host) => _cache.GetOrAdd(host, LoadOrCreateLeaf);

    // Mirrors Titanium's CertificateDiskCache.GetRootCertificateDirectory: on Windows the
    // root lives next to the executable; on mac/Linux under ApplicationData/dev-proxy.
    private static string ResolveConfigDirectory()
    {
        if (OperatingSystem.IsWindows())
        {
            return AppContext.BaseDirectory;
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            ConfigFolderName);
    }

    private X509Certificate2 LoadOrCreateRoot()
    {
        if (File.Exists(_rootPfxPath))
        {
            var existing = TryLoadRoot(_rootPfxPath);
            if (existing is not null)
            {
                _logger.LogDebug("Loaded persisted root certificate from {Path}", _rootPfxPath);
                return existing;
            }

            _logger.LogWarning(
                "Persisted root certificate at {Path} is missing/invalid/expired; regenerating.",
                _rootPfxPath);
        }

        var root = CreateRootCertificate();
        SaveRoot(root);

        // A fresh root invalidates every previously minted leaf (they were signed by the
        // old root), so drop the on-disk leaf cache to avoid serving untrusted leaves.
        PurgeLeafCache();
        return root;
    }

    private static X509Certificate2? TryLoadRoot(string path)
    {
        try
        {
            var cert = X509CertificateLoader.LoadPkcs12(
                File.ReadAllBytes(path),
                string.Empty,
                X509KeyStorageFlags.Exportable);

            var isCa = cert.Extensions
                .OfType<X509BasicConstraintsExtension>()
                .FirstOrDefault()?.CertificateAuthority == true;

            if (cert.HasPrivateKey && isCa && cert.NotAfter > DateTime.Now)
            {
                return cert;
            }

            cert.Dispose();
            return null;
        }
        catch (Exception ex) when (ex is CryptographicException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private void SaveRoot(X509Certificate2 root)
    {
        try
        {
            var directory = Path.GetDirectoryName(_rootPfxPath);
            if (!string.IsNullOrEmpty(directory))
            {
                _ = Directory.CreateDirectory(directory);
            }

            File.WriteAllBytes(_rootPfxPath, root.Export(X509ContentType.Pkcs12, string.Empty));
            _logger.LogInformation("Saved new root certificate to {Path}", _rootPfxPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best-effort: an unwritable location (e.g. read-only install dir) means the
            // root is in-memory only and won't survive a restart, but interception still
            // works this run.
            _logger.LogWarning(ex, "Could not persist root certificate to {Path}", _rootPfxPath);
        }
    }

    private void PurgeLeafCache()
    {
        try
        {
            if (Directory.Exists(_leafDirectory))
            {
                Directory.Delete(_leafDirectory, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not purge stale leaf certificate cache at {Path}", _leafDirectory);
        }
    }

    private X509Certificate2 LoadOrCreateLeaf(string host)
    {
        var leafPath = Path.Combine(_leafDirectory, SanitizeFileName(host) + ".pfx");

        var existing = TryLoadLeaf(leafPath);
        if (existing is not null)
        {
            return existing;
        }

        var leaf = CreateLeafCertificate(host);
        SaveLeaf(leafPath, leaf);
        return leaf;
    }

    private static X509Certificate2? TryLoadLeaf(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var cert = X509CertificateLoader.LoadPkcs12(
                File.ReadAllBytes(path),
                string.Empty,
                X509KeyStorageFlags.Exportable);

            if (cert.HasPrivateKey && cert.NotAfter > DateTime.Now)
            {
                return cert;
            }

            cert.Dispose();
            return null;
        }
        catch (Exception ex) when (ex is CryptographicException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private void SaveLeaf(string path, X509Certificate2 leaf)
    {
        try
        {
            _ = Directory.CreateDirectory(_leafDirectory);
            File.WriteAllBytes(path, leaf.Export(X509ContentType.Pkcs12, string.Empty));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best-effort cache; the in-memory leaf is still returned and used this run.
            _logger.LogTrace(ex, "Could not persist leaf certificate to {Path}", path);
        }
    }

    // Hosts are filename-unsafe on some platforms (IPv6 literals contain ':', wildcard
    // hosts contain '*'). The leaf filename is a local cache key only — it doesn't need
    // to match Titanium's naming since trust derives from the (shared) root, not the leaf.
    private static string SanitizeFileName(string host)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = host.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            if (Array.IndexOf(invalid, chars[i]) >= 0)
            {
                chars[i] = '_';
            }
        }

        return new string(chars);
    }

    private static X509Certificate2 CreateRootCertificate()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            $"CN={RootCertCommonName}",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);

        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));

        // Round-trip through PKCS#12 so the in-memory root carries an exportable private
        // key on every platform (required both to sign leaves and to persist to disk).
        using var selfSigned = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddYears(10));
        return X509CertificateLoader.LoadPkcs12(
            selfSigned.Export(X509ContentType.Pkcs12, string.Empty),
            string.Empty,
            X509KeyStorageFlags.Exportable);
    }

    private X509Certificate2 CreateLeafCertificate(string host)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            $"CN={host}",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);

        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, false));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            [new Oid("1.3.6.1.5.5.7.3.1")], false)); // serverAuth

        var sanBuilder = new SubjectAlternativeNameBuilder();
        if (System.Net.IPAddress.TryParse(host, out var ip))
        {
            sanBuilder.AddIpAddress(ip);
        }
        else
        {
            sanBuilder.AddDnsName(host);
        }
        request.CertificateExtensions.Add(sanBuilder.Build());
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));

        var serialNumber = new byte[8];
        RandomNumberGenerator.Fill(serialNumber);

        // A leaf may not outlive its issuer. When reusing an existing (possibly
        // near-expiry) root, clamp the leaf's validity window to the root's.
        var notBefore = DateTimeOffset.UtcNow.AddDays(-1);
        var rootNotBefore = new DateTimeOffset(_ca.NotBefore.ToUniversalTime());
        if (notBefore < rootNotBefore)
        {
            notBefore = rootNotBefore;
        }

        var notAfter = DateTimeOffset.UtcNow.AddDays(LeafValidityDays);
        var rootNotAfter = new DateTimeOffset(_ca.NotAfter.ToUniversalTime());
        if (notAfter > rootNotAfter)
        {
            notAfter = rootNotAfter;
        }

        using var leaf = request.Create(
            _ca,
            notBefore,
            notAfter,
            serialNumber);

        using var leafWithKey = leaf.CopyWithPrivateKey(rsa);

        // Round-trip through PKCS#12 so the certificate is usable as a server
        // certificate by SslStream on every platform.
        return X509CertificateLoader.LoadPkcs12(
            leafWithKey.Export(X509ContentType.Pkcs12, string.Empty),
            string.Empty,
            X509KeyStorageFlags.Exportable);
    }

    public void Dispose()
    {
        _ca.Dispose();
        foreach (var leaf in _cache.Values)
        {
            leaf.Dispose();
        }
        _cache.Clear();
    }
}
