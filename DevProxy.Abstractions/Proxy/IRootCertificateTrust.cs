// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Security.Cryptography.X509Certificates;

namespace DevProxy.Abstractions.Proxy;

/// <summary>
/// Installs the proxy's root certificate into the operating-system trust store so
/// intercepted HTTPS traffic validates without per-request warnings.
///
/// <para>
/// This is the engine-host boundary for certificate trust: a proxy engine (e.g. the
/// Kestrel engine) owns minting/persisting its root but cannot reference the host's
/// platform trust helpers, so it calls this abstraction. The host supplies the
/// implementation (mac keychain, Windows root store, first-run prompt). Implementations
/// are expected to be idempotent and to honor the user's trust/first-run configuration.
/// </para>
/// </summary>
public interface IRootCertificateTrust
{
    /// <summary>
    /// Ensures <paramref name="rootCertificate"/> is trusted by the OS, subject to the
    /// host's install/first-run policy. Best-effort: failures are logged, not thrown.
    /// </summary>
    void EnsureTrusted(X509Certificate2 rootCertificate);
}
