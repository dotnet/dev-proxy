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

        return services;
    }
}
