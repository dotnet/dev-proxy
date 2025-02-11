// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

namespace Microsoft.DevProxy.Plugins.MinimalPermissions;

internal class GraphPermissionInfo
{
    public string Value { get; set; } = string.Empty;
    public string ScopeType { get; set; } = string.Empty;
    public string ConsentDisplayName { get; set; } = string.Empty;
    public string ConsentDescription { get; set; } = string.Empty;
    public bool IsAdmin { get; set; }
    public bool IsLeastPrivilege { get; set; }
    public bool IsHidden { get; set; }
}