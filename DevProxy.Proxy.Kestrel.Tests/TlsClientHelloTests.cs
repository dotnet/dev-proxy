// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Buffers;
using System.Buffers.Binary;
using System.Text;
using DevProxy.Proxy.Kestrel.Internal;

using Xunit;

namespace DevProxy.Proxy.Kestrel.Tests;

public class TlsClientHelloTests
{
    [Fact]
    public void Parse_NonTlsBytes_ReturnsNotTls()
    {
        var http = Encoding.ASCII.GetBytes("GET / HTTP/1.1\r\nHost: x\r\n\r\n");

        var result = TlsClientHello.Parse(new ReadOnlySequence<byte>(http));

        Assert.Equal(TlsClientHello.ParseStatus.NotTls, result.Status);
    }

    [Fact]
    public void Parse_FewerThanFiveBytes_ReturnsNeedMore()
    {
        var result = TlsClientHello.Parse(new ReadOnlySequence<byte>([0x16, 0x03]));

        Assert.Equal(TlsClientHello.ParseStatus.NeedMore, result.Status);
    }

    [Fact]
    public void Parse_TruncatedRecord_ReturnsNeedMore()
    {
        // Valid record header advertising a longer body than is present.
        var hello = BuildClientHello("example.com", ["h2", "http/1.1"]);
        var truncated = hello.AsSpan(0, hello.Length - 10).ToArray();

        var result = TlsClientHello.Parse(new ReadOnlySequence<byte>(truncated));

        Assert.Equal(TlsClientHello.ParseStatus.NeedMore, result.Status);
    }

    [Fact]
    public void Parse_ExtractsSniAndAlpn()
    {
        var hello = BuildClientHello("api.contoso.com", ["h2", "http/1.1"]);

        var result = TlsClientHello.Parse(new ReadOnlySequence<byte>(hello));

        Assert.Equal(TlsClientHello.ParseStatus.Ok, result.Status);
        Assert.Equal("api.contoso.com", result.ServerName);
        Assert.Equal(["h2", "http/1.1"], result.Alpn);
        Assert.True(result.OffersH2);
        Assert.True(result.OffersHttp11);
        Assert.False(result.IsH2Only);
    }

    [Fact]
    public void Parse_H2OnlyAlpn_IsH2Only()
    {
        var hello = BuildClientHello("grpc.contoso.com", ["h2"]);

        var result = TlsClientHello.Parse(new ReadOnlySequence<byte>(hello));

        Assert.Equal(TlsClientHello.ParseStatus.Ok, result.Status);
        Assert.True(result.IsH2Only);
    }

    [Fact]
    public void Parse_Http11OnlyAlpn_IsNotH2Only()
    {
        var hello = BuildClientHello("contoso.com", ["http/1.1"]);

        var result = TlsClientHello.Parse(new ReadOnlySequence<byte>(hello));

        Assert.Equal(TlsClientHello.ParseStatus.Ok, result.Status);
        Assert.False(result.OffersH2);
        Assert.False(result.IsH2Only);
    }

    [Fact]
    public void Parse_NoAlpnExtension_IsNotH2Only()
    {
        var hello = BuildClientHello("contoso.com", alpn: null);

        var result = TlsClientHello.Parse(new ReadOnlySequence<byte>(hello));

        Assert.Equal(TlsClientHello.ParseStatus.Ok, result.Status);
        Assert.Equal("contoso.com", result.ServerName);
        Assert.Empty(result.Alpn);
        Assert.False(result.IsH2Only);
    }

    [Fact]
    public void Parse_NoExtensionsBlock_ReturnsOkWithNoSniOrAlpn()
    {
        var hello = BuildClientHello(serverName: null, alpn: null);

        var result = TlsClientHello.Parse(new ReadOnlySequence<byte>(hello));

        Assert.Equal(TlsClientHello.ParseStatus.Ok, result.Status);
        Assert.Null(result.ServerName);
        Assert.Empty(result.Alpn);
    }

    [Fact]
    public void Parse_WorksAcrossSegmentedSequence()
    {
        var hello = BuildClientHello("split.contoso.com", ["h2", "http/1.1"]);
        // Split the bytes across two pipe segments to prove the parser tolerates
        // a non-contiguous ReadOnlySequence (the shape PipeReader delivers).
        var sequence = CreateSegmented(hello, hello.Length / 2);

        var result = TlsClientHello.Parse(sequence);

        Assert.Equal(TlsClientHello.ParseStatus.Ok, result.Status);
        Assert.Equal("split.contoso.com", result.ServerName);
        Assert.Equal(["h2", "http/1.1"], result.Alpn);
    }

    private static ReadOnlySequence<byte> CreateSegmented(byte[] data, int splitAt)
    {
        var first = new MemorySegment<byte>(data.AsMemory(0, splitAt));
        var second = first.Append(data.AsMemory(splitAt));
        return new ReadOnlySequence<byte>(first, 0, second, second.Memory.Length);
    }

    // Builds a minimal but well-formed TLS 1.2 ClientHello record carrying the given
    // SNI (optional) and ALPN protocol list (optional). Only the fields the parser
    // reads are populated; everything else uses valid placeholder values.
    private static byte[] BuildClientHello(string? serverName, IReadOnlyList<string>? alpn)
    {
        var extensions = new List<byte>();
        if (serverName is not null)
        {
            var nameBytes = Encoding.ASCII.GetBytes(serverName);
            var serverNameList = new List<byte> { 0x00 }; // host_name type
            AppendUInt16(serverNameList, (ushort)nameBytes.Length);
            serverNameList.AddRange(nameBytes);

            var sniData = new List<byte>();
            AppendUInt16(sniData, (ushort)serverNameList.Count); // server_name_list length
            sniData.AddRange(serverNameList);

            AppendExtension(extensions, 0x0000, sniData);
        }

        if (alpn is not null)
        {
            var protocolList = new List<byte>();
            foreach (var proto in alpn)
            {
                var protoBytes = Encoding.ASCII.GetBytes(proto);
                protocolList.Add((byte)protoBytes.Length);
                protocolList.AddRange(protoBytes);
            }

            var alpnData = new List<byte>();
            AppendUInt16(alpnData, (ushort)protocolList.Count); // ProtocolNameList length
            alpnData.AddRange(protocolList);

            AppendExtension(extensions, 0x0010, alpnData);
        }

        var body = new List<byte>();
        body.AddRange([0x03, 0x03]);          // client_version TLS 1.2
        body.AddRange(new byte[32]);          // random
        body.Add(0x00);                       // session_id length
        AppendUInt16(body, 2);                // cipher_suites length
        body.AddRange([0x00, 0x2f]);          // one cipher suite
        body.Add(0x01);                       // compression methods length
        body.Add(0x00);                       // null compression

        if (serverName is not null || alpn is not null)
        {
            AppendUInt16(body, (ushort)extensions.Count); // extensions length
            body.AddRange(extensions);
        }

        var handshake = new List<byte> { 0x01 }; // ClientHello
        AppendUInt24(handshake, body.Count);
        handshake.AddRange(body);

        var record = new List<byte> { 0x16, 0x03, 0x01 }; // handshake, TLS 1.0 record version
        AppendUInt16(record, (ushort)handshake.Count);
        record.AddRange(handshake);

        return [.. record];
    }

    private static void AppendExtension(List<byte> extensions, ushort type, List<byte> data)
    {
        AppendUInt16(extensions, type);
        AppendUInt16(extensions, (ushort)data.Count);
        extensions.AddRange(data);
    }

    private static void AppendUInt16(List<byte> target, ushort value)
    {
        Span<byte> buffer = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(buffer, value);
        target.AddRange(buffer.ToArray());
    }

    private static void AppendUInt24(List<byte> target, int value)
    {
        target.Add((byte)((value >> 16) & 0xFF));
        target.Add((byte)((value >> 8) & 0xFF));
        target.Add((byte)(value & 0xFF));
    }

    private sealed class MemorySegment<T> : ReadOnlySequenceSegment<T>
    {
        public MemorySegment(ReadOnlyMemory<T> memory) => Memory = memory;

        public MemorySegment<T> Append(ReadOnlyMemory<T> memory)
        {
            var segment = new MemorySegment<T>(memory) { RunningIndex = RunningIndex + Memory.Length };
            Next = segment;
            return segment;
        }
    }
}
