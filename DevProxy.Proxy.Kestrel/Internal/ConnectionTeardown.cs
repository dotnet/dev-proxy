// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Net.Sockets;
using Microsoft.AspNetCore.Connections;

namespace DevProxy.Proxy.Kestrel.Internal;

/// <summary>
/// Classifies exceptions that are the normal consequence of a peer closing or resetting
/// the connection (or the connection being cancelled) rather than a real fault. Every
/// read/copy/write boundary against the client or origin socket can surface one of these
/// when the other end goes away mid-exchange; treating them as EOF/close keeps client
/// disconnects from showing up as noisy "unhandled exception" errors.
///
/// <code>
///   client/origin closes ─┬─ ConnectionResetException  (: IOException)  ─┐
///   client/origin aborts ─┼─ ConnectionAbortedException (: Operation…)  ─┤
///   cancellation token ───┼─ OperationCanceledException                  ├─► IsExpected = true
///   socket layer error ───┼─ SocketException                             │
///   stream EOF/reset ─────┴─ IOException                                ─┘
///   anything else ──────────────────────────────────────────────────────► IsExpected = false
/// </code>
///
/// <para>
/// <c>ConnectionResetException</c> derives from <see cref="IOException"/> and
/// <c>ConnectionAbortedException</c> from <see cref="OperationCanceledException"/>, so the
/// base arms already cover them; they are listed explicitly for readability.
/// </para>
/// </summary>
internal static class ConnectionTeardown
{
    public static bool IsExpected(Exception exception) => exception switch
    {
        ConnectionResetException => true,
        ConnectionAbortedException => true,
        OperationCanceledException => true,
        IOException => true,
        SocketException => true,
        _ => false,
    };
}
