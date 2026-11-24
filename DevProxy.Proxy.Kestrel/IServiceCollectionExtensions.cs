// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Security.Cryptography.X509Certificates;
using DevProxy.Proxy.Kestrel.Internal;
using Microsoft.Extensions.Logging;

#pragma warning disable IDE0130
namespace Microsoft.Extensions.DependencyInjection;
#pragma warning restore IDE0130

/// <summary>
/// DI wiring for the Kestrel proxy engine's certificate authority.
///
/// <para>
/// Registers the <see cref="CertificateAuthority"/> as a single shared singleton and
/// exposes its root <see cref="X509Certificate2"/> — so the engine (TLS termination),
/// the <c>cert</c> command (trust/remove), the cert-download API, and the Entra mock
/// plugin (signing-key chain) all resolve the <b>same</b> certificate rather than each
/// loading its own copy.
/// </para>
///
/// <para>
/// The root is loaded or created on first use, not when the CA is constructed, so
/// commands that never need it (such as <c>--version</c>) don't write it to disk.
/// </para>
/// </summary>
public static class KestrelProxyServiceCollectionExtensions
{
    public static IServiceCollection AddKestrelCertificateAuthority(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        _ = services.AddSingleton(sp =>
            CertificateAuthority.CreateDefault(
                sp.GetRequiredService<ILoggerFactory>().CreateLogger<CertificateAuthority>()));
        _ = services.AddSingleton(sp => sp.GetRequiredService<CertificateAuthority>().RootCertificate);
        // Lets consumers built eagerly (e.g. CLI commands) defer loading/creating the root
        // until they actually need it.
        _ = services.AddSingleton(sp => new Lazy<X509Certificate2>(sp.GetRequiredService<X509Certificate2>));

        return services;
    }
}
