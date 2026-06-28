// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Net.Sockets;

namespace DevProxy.Integration.Tests;

/// <summary>
/// Test networking helpers.
/// </summary>
internal static class NetUtil
{
    /// <summary>
    /// Reserves a free localhost TCP port by binding to port 0, reading the
    /// OS-assigned port, then releasing it. There is an inherent (tiny) race
    /// between release and re-bind, but it is acceptable for hermetic tests.
    /// </summary>
    public static int GetFreePort()
    {
        var listener = new TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }
}
