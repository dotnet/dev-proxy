// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

namespace Microsoft.DevProxy.Plugins.MinimalPermissions;

public class ApiOperation
{
    public required string Method { get; init; }
    public required string OriginalUrl { get; init; }
    public required string TokenizedUrl { get; init; }
}