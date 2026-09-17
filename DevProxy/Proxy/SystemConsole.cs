// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

namespace DevProxy.Proxy;

/// <summary>
/// Default <see cref="ISystemConsole"/> implementation backed by
/// <see cref="System.Console"/>. Keys are read with <c>intercept: true</c> so
/// they aren't echoed to the terminal (matching the legacy engine's behavior).
/// </summary>
internal sealed class SystemConsole : ISystemConsole
{
    public bool IsInputRedirected => Console.IsInputRedirected;

    public bool KeyAvailable => Console.KeyAvailable;

    public ConsoleKey ReadKey() => Console.ReadKey(intercept: true).Key;

    public void Clear() => Console.Clear();

    public void WriteLine(string value) => Console.WriteLine(value);
}
