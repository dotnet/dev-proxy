// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

namespace DevProxy.Proxy;

/// <summary>
/// A thin seam over <see cref="System.Console"/> so the interactive hotkey
/// handling can be unit-tested without a real terminal. Named
/// <c>ISystemConsole</c> (not <c>IConsole</c>) to avoid colliding with
/// <c>System.CommandLine.IConsole</c>, which is also referenced by the host.
/// </summary>
internal interface ISystemConsole
{
    /// <summary>Whether stdin is redirected (piped/non-interactive).</summary>
    bool IsInputRedirected { get; }

    /// <summary>Whether a key press is waiting to be read.</summary>
    bool KeyAvailable { get; }

    /// <summary>Reads the next key without echoing it to the terminal.</summary>
    ConsoleKey ReadKey();

    /// <summary>Clears the terminal.</summary>
    void Clear();

    /// <summary>Writes a line to stdout.</summary>
    void WriteLine(string value);
}
