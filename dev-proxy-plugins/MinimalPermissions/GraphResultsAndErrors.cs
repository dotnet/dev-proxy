// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

namespace Microsoft.DevProxy.Plugins.MinimalPermissions;

internal class GraphResultsAndErrors
{
    public GraphPermissionInfo[]? Results { get; set; }
    public GraphPermissionError[]? Errors { get; set; }
}