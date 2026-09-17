// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Proxy.Kestrel.Internal;

using Xunit;

namespace DevProxy.Proxy.Kestrel.Tests;

public class ProcessFilterTests
{
    private static ProcessFilter Filter(
        IEnumerable<int>? pids = null,
        IEnumerable<string>? names = null,
        Func<int, int?>? resolvePid = null,
        Func<int, string?>? resolveName = null) =>
        new(pids ?? [], names ?? [], resolvePid, resolveName);

    [Fact]
    public void IsEmpty_True_WhenNoFilterConfigured()
    {
        Assert.True(Filter().IsEmpty);
    }

    [Fact]
    public void IsEmpty_False_WhenPidsConfigured()
    {
        Assert.False(Filter(pids: [123]).IsEmpty);
    }

    [Fact]
    public void IsEmpty_False_WhenNamesConfigured()
    {
        Assert.False(Filter(names: ["node"]).IsEmpty);
    }

    [Fact]
    public void IsWatchedProcess_True_WhenNoFilterConfigured()
    {
        // No filter ⇒ every process watched; resolver must never even be consulted.
        var filter = Filter(resolvePid: _ => throw new InvalidOperationException("should not resolve"));
        Assert.True(filter.IsWatchedProcess(54321));
    }

    [Fact]
    public void IsWatchedProcess_True_WhenPidMatches()
    {
        var filter = Filter(pids: [4242], resolvePid: _ => 4242);
        Assert.True(filter.IsWatchedProcess(54321));
    }

    [Fact]
    public void IsWatchedProcess_False_WhenPidDoesNotMatch()
    {
        var filter = Filter(pids: [4242], resolvePid: _ => 9999);
        Assert.False(filter.IsWatchedProcess(54321));
    }

    [Fact]
    public void IsWatchedProcess_False_WhenPidUnresolved()
    {
        var filter = Filter(pids: [4242], resolvePid: _ => null);
        Assert.False(filter.IsWatchedProcess(54321));
    }

    [Fact]
    public void IsWatchedProcess_False_WhenResolverReturnsMinusOne()
    {
        // -1 is the "not found" sentinel from the listing tools; treat as unresolved.
        var filter = Filter(pids: [4242], resolvePid: _ => -1);
        Assert.False(filter.IsWatchedProcess(54321));
    }

    [Fact]
    public void IsWatchedProcess_True_WhenProcessNameMatches()
    {
        var filter = Filter(names: ["node"], resolvePid: _ => 4242, resolveName: _ => "node");
        Assert.True(filter.IsWatchedProcess(54321));
    }

    [Fact]
    public void IsWatchedProcess_False_WhenProcessNameDiffers()
    {
        var filter = Filter(names: ["node"], resolvePid: _ => 4242, resolveName: _ => "chrome");
        Assert.False(filter.IsWatchedProcess(54321));
    }

    [Fact]
    public void IsWatchedProcess_NameMatchIsCaseSensitive_MatchingTitanium()
    {
        var filter = Filter(names: ["node"], resolvePid: _ => 4242, resolveName: _ => "Node");
        Assert.False(filter.IsWatchedProcess(54321));
    }

    [Fact]
    public void IsWatchedProcess_PidWins_WithoutResolvingName()
    {
        var filter = Filter(
            pids: [4242],
            names: ["node"],
            resolvePid: _ => 4242,
            resolveName: _ => throw new InvalidOperationException("name lookup not needed"));
        Assert.True(filter.IsWatchedProcess(54321));
    }
}

public class LsofParserTests
{
    // Realistic `lsof -i :54321` output. The client process (node) owns the
    // …:54321->… socket; the proxy (dotnet) owns the reverse …->…:54321 socket.
    private const string Sample =
        "COMMAND   PID   USER   FD   TYPE  DEVICE SIZE/OFF NODE NAME\n" +
        "node    4242 waldek   23u  IPv4 0x1234      0t0  TCP 127.0.0.1:54321->127.0.0.1:8897 (ESTABLISHED)\n" +
        "dotnet  9001 waldek   30u  IPv4 0x5678      0t0  TCP 127.0.0.1:8897->127.0.0.1:54321 (ESTABLISHED)\n";

    [Fact]
    public void ParsePid_ReturnsClientProcessPid()
    {
        Assert.Equal(4242, LsofParser.ParsePid(Sample, 54321));
    }

    [Fact]
    public void ParsePid_SelectsSourcePortLine_NotDestination()
    {
        // 54321 appears as the client's SOURCE port (":54321->", matches) and as the
        // proxy's DESTINATION ("->...:54321", must NOT match). The marker selects the
        // client process (4242), not the proxy (9001).
        Assert.Equal(4242, LsofParser.ParsePid(Sample, 54321));
    }

    [Fact]
    public void ParsePid_ReturnsNull_WhenPortAbsent()
    {
        Assert.Null(LsofParser.ParsePid(Sample, 11111));
    }

    [Fact]
    public void ParsePid_ReturnsNull_OnEmptyOutput()
    {
        Assert.Null(LsofParser.ParsePid("", 54321));
    }
}

public class NetstatParserTests
{
    // Realistic `netstat -ano -p tcp`. The client socket's LOCAL address is …:54321.
    private const string Sample =
        "\nActive Connections\n\n" +
        "  Proto  Local Address          Foreign Address        State           PID\n" +
        "  TCP    127.0.0.1:54321        127.0.0.1:8897         ESTABLISHED     4242\n" +
        "  TCP    127.0.0.1:8897         127.0.0.1:54321        ESTABLISHED     9001\n";

    [Fact]
    public void ParsePid_ReturnsClientProcessPid_ByLocalPort()
    {
        Assert.Equal(4242, NetstatParser.ParsePid(Sample, 54321));
    }

    [Fact]
    public void ParsePid_MatchesByLocalPort_NotForeignPort()
    {
        // Matching is by LOCAL address port. Querying the proxy's own listen port (8897)
        // therefore returns the proxy row's PID — in practice we always query the client's
        // source port (54321), which uniquely identifies the client socket.
        Assert.Equal(9001, NetstatParser.ParsePid(Sample, 8897));
    }

    [Fact]
    public void ParsePid_ReturnsNull_WhenPortAbsent()
    {
        Assert.Null(NetstatParser.ParsePid(Sample, 11111));
    }

    [Fact]
    public void ParsePid_HandlesIPv6LocalAddress()
    {
        const string ipv6 =
            "  Proto  Local Address          Foreign Address        State           PID\n" +
            "  TCP    [::1]:54321            [::1]:8897             ESTABLISHED     7777\n";
        Assert.Equal(7777, NetstatParser.ParsePid(ipv6, 54321));
    }

    [Fact]
    public void ParsePid_ReturnsNull_OnEmptyOutput()
    {
        Assert.Null(NetstatParser.ParsePid("", 54321));
    }
}
