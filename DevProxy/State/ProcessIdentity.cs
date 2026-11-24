// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

namespace DevProxy.State;

/// <summary>
/// Result of comparing a running process with the identity recorded in a state file.
/// </summary>
internal enum ProcessIdentity
{
    /// <summary>The state has no recorded start time (written by an older version).</summary>
    NotRecorded,
    /// <summary>The process start time matches the recorded value.</summary>
    Match,
    /// <summary>The process start time differs; the PID was reused.</summary>
    Mismatch,
    /// <summary>The process start time couldn't be read.</summary>
    Unverifiable
}
