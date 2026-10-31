// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Text;
using DevProxy.Stdio;
using Xunit;

namespace DevProxy.Tests;

public class StdioLineFramerTests
{
    private static string[] AsStrings(IEnumerable<byte[]> messages) =>
        [.. messages.Select(m => Encoding.UTF8.GetString(m))];

    [Fact]
    public void Append_MultipleLinesInOneChunk_ReturnsEachLine()
    {
        var framer = new StdioLineFramer();

        var messages = framer.Append(Encoding.UTF8.GetBytes(
            "{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}\n{\"jsonrpc\":\"2.0\",\"id\":2}\n"));

        Assert.Equal(
            ["{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}\n", "{\"jsonrpc\":\"2.0\",\"id\":2}\n"],
            AsStrings(messages));
        Assert.Null(framer.Flush());
    }

    [Fact]
    public void Append_LineSplitAcrossChunks_ReturnsLineOnceComplete()
    {
        var framer = new StdioLineFramer();

        Assert.Empty(framer.Append(Encoding.UTF8.GetBytes("{\"id\":")));
        Assert.Empty(framer.Append(Encoding.UTF8.GetBytes("1")));
        var messages = framer.Append(Encoding.UTF8.GetBytes("}\n{\"id\":2"));

        Assert.Equal(["{\"id\":1}\n"], AsStrings(messages));
        Assert.Equal("{\"id\":2", Encoding.UTF8.GetString(framer.Flush()!));
        Assert.Null(framer.Flush());
    }

    [Fact]
    public void Append_CrLf_KeepsLineEndingIntact()
    {
        var framer = new StdioLineFramer();

        var messages = framer.Append(Encoding.UTF8.GetBytes("a\r\nb\r\n"));

        Assert.Equal(["a\r\n", "b\r\n"], AsStrings(messages));
    }

    [Fact]
    public void Append_EmptyInput_ReturnsNothing()
    {
        var framer = new StdioLineFramer();

        Assert.Empty(framer.Append([]));
        Assert.Null(framer.Flush());
    }

    [Fact]
    public void Append_LineLargerThanReadBuffer_IsReturnedAsSingleMessage()
    {
        var framer = new StdioLineFramer();
        var line = new string('x', 10_000) + "\n";
        var bytes = Encoding.UTF8.GetBytes(line);

        var messages = new List<byte[]>();
        for (var offset = 0; offset < bytes.Length; offset += 4096)
        {
            var length = Math.Min(4096, bytes.Length - offset);
            messages.AddRange(framer.Append(bytes.AsSpan(offset, length)));
        }

        Assert.Equal([line], AsStrings(messages));
    }
}
