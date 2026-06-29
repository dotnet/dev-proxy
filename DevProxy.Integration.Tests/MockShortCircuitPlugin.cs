// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Net;
using DevProxy.Abstractions.Plugins;
using DevProxy.Abstractions.Proxy;
using DevProxy.Abstractions.Proxy.Http;
using Microsoft.Extensions.Logging.Abstractions;

namespace DevProxy.Integration.Tests;

/// <summary>
/// Minimal plugin that short-circuits every matched request with a canned response —
/// the mocking primitive (<c>GenericResponse</c> in Titanium, <c>Respond</c> on the
/// canonical session). Used to prove the Kestrel engine honours a plugin-set response
/// without ever contacting the origin.
/// </summary>
internal sealed class MockShortCircuitPlugin(ISet<UrlToWatch> urlsToWatch)
    : BasePlugin(NullLogger.Instance, urlsToWatch)
{
    public const string MockBody = "mocked-by-plugin";

    public const int MockStatus = 418;

    public override string Name => nameof(MockShortCircuitPlugin);

    public override Task BeforeRequestAsync(
        ProxyRequestArgs e, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(e);
        if (!e.ShouldExecute(UrlsToWatch))
        {
            return Task.CompletedTask;
        }

        e.ProxySession.Respond(
            MockBody,
            (HttpStatusCode)MockStatus,
            [new HttpHeader("X-Mocked", "true")]);
        e.ResponseState.HasBeenSet = true;
        return Task.CompletedTask;
    }
}
