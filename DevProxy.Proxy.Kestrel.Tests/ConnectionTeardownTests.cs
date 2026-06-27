// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Net.Sockets;
using DevProxy.Proxy.Kestrel.Internal;
using Microsoft.AspNetCore.Connections;
using Xunit;

namespace DevProxy.Proxy.Kestrel.Tests;

public class ConnectionTeardownTests
{
    // ── Expected (normal close/cancel) ──────────────────────────────────
    [Fact]
    public void IsExpected_OperationCanceled_True() =>
        Assert.True(ConnectionTeardown.IsExpected(new OperationCanceledException()));

    [Fact]
    public void IsExpected_TaskCanceled_True() =>
        Assert.True(ConnectionTeardown.IsExpected(new TaskCanceledException()));

    [Fact]
    public void IsExpected_IOException_True() =>
        Assert.True(ConnectionTeardown.IsExpected(new IOException("broken pipe")));

    [Fact]
    public void IsExpected_SocketException_True() =>
        Assert.True(ConnectionTeardown.IsExpected(new SocketException((int)SocketError.ConnectionReset)));

    [Fact]
    public void IsExpected_ConnectionResetException_True() =>
        Assert.True(ConnectionTeardown.IsExpected(new ConnectionResetException("reset")));

    [Fact]
    public void IsExpected_ConnectionAbortedException_True() =>
        Assert.True(ConnectionTeardown.IsExpected(new ConnectionAbortedException()));

    // ── Not expected (real faults) ──────────────────────────────────────
    [Fact]
    public void IsExpected_InvalidOperationException_False() =>
        Assert.False(ConnectionTeardown.IsExpected(new InvalidOperationException()));

    [Fact]
    public void IsExpected_NullReferenceException_False() =>
        Assert.False(ConnectionTeardown.IsExpected(new NullReferenceException()));

    [Fact]
    public void IsExpected_UnrelatedException_False() =>
        Assert.False(ConnectionTeardown.IsExpected(new FormatException("boom")));
}
