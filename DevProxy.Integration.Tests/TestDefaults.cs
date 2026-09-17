// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

namespace DevProxy.Integration.Tests;

/// <summary>
/// Shared, process-wide test defaults. A single <see cref="HttpClient"/> is reused
/// across plugin constructions and the DI host to avoid socket exhaustion.
/// </summary>
internal static class TestDefaults
{
    public static readonly HttpClient HttpClient = new();
}
