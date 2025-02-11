// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Security.Cryptography.X509Certificates;
using Microsoft.DevProxy.Abstractions;
using Microsoft.DevProxy.Abstractions.LanguageModel;

namespace Microsoft.DevProxy;

internal class ProxyContext(IProxyConfiguration configuration, X509Certificate2? certificate, ILanguageModelClient languageModelClient) : IProxyContext
{
    public IProxyConfiguration Configuration { get; } = configuration ?? throw new ArgumentNullException(nameof(configuration));
    public X509Certificate2? Certificate { get; } = certificate;
    public ILanguageModelClient LanguageModelClient { get; } = languageModelClient;
}
