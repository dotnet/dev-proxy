// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Xunit;

namespace DevProxy.Integration.Tests;

/// <summary>
/// A <see cref="TheoryAttribute"/> that is skipped on Windows with the given reason.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
internal sealed class SkipOnWindowsTheoryAttribute : TheoryAttribute
{
    public SkipOnWindowsTheoryAttribute(string reason)
    {
        if (OperatingSystem.IsWindows())
        {
            Skip = reason;
        }
    }
}
