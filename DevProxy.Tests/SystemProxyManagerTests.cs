// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Proxy;

using Xunit;

namespace DevProxy.Tests;

public class SystemProxyManagerTests
{
    [Fact]
    public void CreateToggleScriptStartInfo_PreservesPathAndArgumentsContainingSpaces()
    {
        var startInfo = SystemProxyManager.CreateToggleScriptStartInfo(
            "/Applications/Dev Proxy/toggle-proxy.sh",
            ["on", "proxy host", "8897"]);

        Assert.Equal("/bin/bash", startInfo.FileName);
        Assert.Empty(startInfo.Arguments);
        Assert.Equal(
            ["/Applications/Dev Proxy/toggle-proxy.sh", "on", "proxy host", "8897"],
            startInfo.ArgumentList);
    }
}
