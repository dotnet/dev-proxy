// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Net;
using System.Net.Sockets;
using DevProxy.Proxy.Kestrel.Internal;

using Xunit;

namespace DevProxy.Proxy.Kestrel.Tests;

// Covers the two parity rows that an engine swap could silently regress:
//   • upstream timeout  → 504 Gateway Timeout (NOT a silent client-style teardown)
//   • invalid upstream cert / any other origin failure → 502 Bad Gateway
// plus the genuine-client-cancellation path, which must stay silent.
public class UpstreamFailureTests
{
    [Fact]
    public void Timeout_TokenNotCancelled_IsGatewayTimeout()
    {
        // An HttpClient request timeout surfaces as TaskCanceledException while the
        // connection's own token is NOT cancelled.
        var outcome = UpstreamFailure.Classify(new TaskCanceledException(), clientCancelled: false);

        Assert.NotNull(outcome);
        Assert.Equal(HttpStatusCode.GatewayTimeout, outcome!.Value.Status);
        Assert.Equal("Upstream request timed out", outcome.Value.Message);
    }

    [Fact]
    public void OperationCanceled_TokenNotCancelled_IsGatewayTimeout()
    {
        var outcome = UpstreamFailure.Classify(new OperationCanceledException(), clientCancelled: false);

        Assert.NotNull(outcome);
        Assert.Equal(HttpStatusCode.GatewayTimeout, outcome!.Value.Status);
    }

    [Fact]
    public void ClientCancellation_TokenCancelled_IsSilent()
    {
        // The client went away mid-forward — normal teardown, no response written.
        var outcome = UpstreamFailure.Classify(new TaskCanceledException(), clientCancelled: true);

        Assert.Null(outcome);
    }

    [Fact]
    public void InvalidUpstreamCertificate_IsBadGateway()
    {
        // A failed TLS handshake to the origin (e.g. untrusted/expired upstream cert)
        // surfaces as HttpRequestException from SocketsHttpHandler.
        var tlsFailure = new HttpRequestException("The remote certificate is invalid.");

        var outcome = UpstreamFailure.Classify(tlsFailure, clientCancelled: false);

        Assert.NotNull(outcome);
        Assert.Equal(HttpStatusCode.BadGateway, outcome!.Value.Status);
        Assert.Equal("Upstream request failed", outcome.Value.Message);
    }

    [Fact]
    public void ConnectionRefused_IsBadGateway()
    {
        var outcome = UpstreamFailure.Classify(
            new HttpRequestException("connect failed", new SocketException((int)SocketError.ConnectionRefused)),
            clientCancelled: false);

        Assert.NotNull(outcome);
        Assert.Equal(HttpStatusCode.BadGateway, outcome!.Value.Status);
    }

    [Fact]
    public void NonCancellation_IsBadGateway_EvenWhenClientCancelled()
    {
        // A real origin fault is reported as a gateway error regardless of the token —
        // only OperationCanceledException is treated as a possible client teardown.
        var outcome = UpstreamFailure.Classify(new HttpRequestException("boom"), clientCancelled: true);

        Assert.NotNull(outcome);
        Assert.Equal(HttpStatusCode.BadGateway, outcome!.Value.Status);
    }

    [Fact]
    public void NullException_Throws() =>
        Assert.Throws<ArgumentNullException>(() => UpstreamFailure.Classify(null!, clientCancelled: false));
}
