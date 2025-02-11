// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

namespace Microsoft.DevProxy.Plugins.MinimalPermissions;

public class ApiPermissionsInfo
{
    public required List<string> TokenPermissions { get; init; }
    public required List<ApiOperation> OperationsFromRequests { get; init; }
    public required string[] MinimalScopes { get; init; }
    public required string[] UnmatchedOperations { get; init; }
    public required List<ApiPermissionError> Errors { get; init; }
}