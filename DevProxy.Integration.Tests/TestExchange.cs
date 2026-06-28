// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Net;
using System.Text;
using DevProxy.Abstractions.Proxy;
using DevProxy.Abstractions.Proxy.Http;
using DevProxy.Proxy.Kestrel.Http;

namespace DevProxy.Integration.Tests;

/// <summary>
/// Builds the engine's real canonical exchange types (<see cref="CanonicalProxySession"/>,
/// <see cref="MutableHttpRequest"/>, <see cref="MutableHttpResponse"/>) so plugin hooks can
/// be driven directly. This is the high-fidelity path for plugins gated on a fixed upstream
/// host (e.g. <c>graph.microsoft.com</c>) that the loopback <see cref="FakeOrigin"/> cannot
/// impersonate through real engine routing — the session object is byte-identical to what the
/// engine constructs, so the test exercises the migrated plugin against the production model.
///
/// <code>
///   MutableHttpRequest ─┐
///                       ├─► CanonicalProxySession ─► ProxyRequestArgs  (BeforeRequest)
///   (+ MutableHttpResponse via SetResponse) ───────► ProxyResponseArgs (Before/AfterResponse)
/// </code>
/// </summary>
internal sealed class TestExchange
{
    public CanonicalProxySession Session { get; }
    public ResponseState State { get; } = new();

    private TestExchange(CanonicalProxySession session) => Session = session;

    public ProxyRequestArgs RequestArgs => new(Session, State);
    public ProxyResponseArgs ResponseArgs => new(Session, State);

    public static TestExchange Request(
        string method,
        string url,
        IEnumerable<(string Name, string Value)>? headers = null,
        string? body = null)
    {
        var collection = new HeaderCollection();
        if (headers is not null)
        {
            foreach (var (name, value) in headers)
            {
                collection.Add(new HttpHeader(name, value));
            }
        }

        ReadOnlyMemory<byte> bodyBytes = body is null
            ? ReadOnlyMemory<byte>.Empty
            : Encoding.UTF8.GetBytes(body);

        var request = new MutableHttpRequest(
            method,
            new Uri(url, UriKind.Absolute),
            HttpVersion.Version11,
            collection,
            bodyBytes);

        return new TestExchange(new CanonicalProxySession(Guid.NewGuid().ToString(), request, processId: null));
    }

    public TestExchange WithResponse(
        HttpStatusCode statusCode,
        IEnumerable<(string Name, string Value)>? headers = null,
        string? body = null)
    {
        var collection = new HeaderCollection();
        if (headers is not null)
        {
            foreach (var (name, value) in headers)
            {
                collection.Add(new HttpHeader(name, value));
            }
        }

        ReadOnlyMemory<byte> bodyBytes = body is null
            ? ReadOnlyMemory<byte>.Empty
            : Encoding.UTF8.GetBytes(body);

        Session.SetOriginResponse(new MutableHttpResponse(
            statusCode,
            HttpVersion.Version11,
            collection,
            bodyBytes));
        return this;
    }
}
