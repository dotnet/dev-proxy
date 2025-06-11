// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

namespace DevProxy.Plugins.Models;

public class ApiPermissionsInfo
{
    public required IEnumerable<ApiPermissionError> Errors { get; init; }
    public required IEnumerable<string> MinimalScopes { get; init; }
    public required IEnumerable<ApiOperation> OperationsFromRequests { get; init; }
    public required IEnumerable<string> TokenPermissions { get; init; }
    public required IEnumerable<string> UnmatchedOperations { get; init; }
}