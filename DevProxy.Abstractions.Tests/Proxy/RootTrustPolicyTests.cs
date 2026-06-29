// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Abstractions.Proxy;
using Xunit;

namespace DevProxy.Abstractions.Tests.Proxy;

public class RootTrustPolicyTests
{
    // ── installCert gate ────────────────────────────────────────────────
    [Theory]
    [InlineData(true, false)]   // mac
    [InlineData(false, true)]   // windows
    [InlineData(false, false)]  // linux
    public void Decide_InstallCertFalse_AlwaysSkips(bool isMac, bool isWindows)
    {
        var action = RootTrustPolicy.Decide(
            isMac, isWindows, installCert: false, noFirstRun: false, isFirstRun: true, firstRunAnswer: "y");

        Assert.Equal(RootTrustAction.Skip, action);
    }

    // ── Windows ─────────────────────────────────────────────────────────
    [Theory]
    [InlineData(true, false)]   // first run, ignored on Windows
    [InlineData(false, true)]   // noFirstRun, ignored on Windows
    public void Decide_Windows_InstallCert_AlwaysTrustsStore(bool isFirstRun, bool noFirstRun)
    {
        var action = RootTrustPolicy.Decide(
            isMac: false, isWindows: true, installCert: true, noFirstRun: noFirstRun, isFirstRun: isFirstRun, firstRunAnswer: null);

        Assert.Equal(RootTrustAction.TrustWindowsStore, action);
    }

    // ── macOS first-run flow ────────────────────────────────────────────
    [Fact]
    public void Decide_Mac_FirstRun_EmptyAnswer_Trusts()
    {
        var action = RootTrustPolicy.Decide(
            isMac: true, isWindows: false, installCert: true, noFirstRun: false, isFirstRun: true, firstRunAnswer: "");

        Assert.Equal(RootTrustAction.TrustMacKeychain, action);
    }

    [Theory]
    [InlineData("y")]
    [InlineData("Y")]
    [InlineData("yes")]
    [InlineData("anything")]
    public void Decide_Mac_FirstRun_NonNoAnswer_Trusts(string answer)
    {
        var action = RootTrustPolicy.Decide(
            isMac: true, isWindows: false, installCert: true, noFirstRun: false, isFirstRun: true, firstRunAnswer: answer);

        Assert.Equal(RootTrustAction.TrustMacKeychain, action);
    }

    [Theory]
    [InlineData("n")]
    [InlineData("N")]
    [InlineData(" n ")]
    public void Decide_Mac_FirstRun_AnswerNo_Skips(string answer)
    {
        var action = RootTrustPolicy.Decide(
            isMac: true, isWindows: false, installCert: true, noFirstRun: false, isFirstRun: true, firstRunAnswer: answer);

        Assert.Equal(RootTrustAction.Skip, action);
    }

    [Fact]
    public void Decide_Mac_NotFirstRun_Skips()
    {
        var action = RootTrustPolicy.Decide(
            isMac: true, isWindows: false, installCert: true, noFirstRun: false, isFirstRun: false, firstRunAnswer: "y");

        Assert.Equal(RootTrustAction.Skip, action);
    }

    [Fact]
    public void Decide_Mac_NoFirstRun_Skips()
    {
        var action = RootTrustPolicy.Decide(
            isMac: true, isWindows: false, installCert: true, noFirstRun: true, isFirstRun: true, firstRunAnswer: "y");

        Assert.Equal(RootTrustAction.Skip, action);
    }

    // ── Linux ───────────────────────────────────────────────────────────
    [Fact]
    public void Decide_Linux_InstallCert_IsManual()
    {
        var action = RootTrustPolicy.Decide(
            isMac: false, isWindows: false, installCert: true, noFirstRun: false, isFirstRun: true, firstRunAnswer: "y");

        Assert.Equal(RootTrustAction.ManualLinux, action);
    }
}
