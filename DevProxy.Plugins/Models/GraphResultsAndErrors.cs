// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

namespace DevProxy.Plugins.Models;

internal sealed class GraphResultsAndErrors
{
    public GraphPermissionError[]? Errors { get; set; }
    public GraphPermissionInfo[]? Results { get; set; }
}