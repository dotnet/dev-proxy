// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Buffers;
using System.Buffers.Binary;
using System.Text;

namespace DevProxy.Proxy.Kestrel.Internal;

/// <summary>
/// Minimal, tolerant TLS <c>ClientHello</c> parser. Extracts the SNI (server name)
/// and the ALPN protocol list so the proxy can decide — <b>before</b> terminating
/// TLS — whether to MITM (advertise <c>http/1.1</c>) or blind-tunnel (h2-only / gRPC).
/// Deliberately NOT a TLS stack: it reads just enough of the first handshake record
/// to make the decrypt-or-tunnel decision.
///
/// <para>
/// Wire layout walked here (all multi-byte integers big-endian):
/// <code>
/// TLS record:    type(1)=0x16  version(2)  length(2)  ── fragment ──
/// handshake:     type(1)=0x01(ClientHello)  length(3)
///                client_version(2)  random(32)
///                session_id:      len(1)  bytes
///                cipher_suites:   len(2)  bytes
///                compression:     len(1)  bytes
///                extensions:      len(2)  [ type(2) len(2) data ]*
///   SNI ext      0x0000:  list_len(2)  type(1)=0  name_len(2)  name
///   ALPN ext     0x0010:  list_len(2)  [ proto_len(1) proto ]*
/// </code>
/// </para>
/// </summary>
internal static class TlsClientHello
{
    private const byte HandshakeRecordType = 0x16;
    private const byte ClientHelloType = 0x01;
    private const ushort ServerNameExtension = 0x0000;
    private const ushort AlpnExtension = 0x0010;
    private const int MaxScanBytes = 8192;

    internal enum ParseStatus
    {
        /// <summary>Not enough bytes buffered yet; read more and retry.</summary>
        NeedMore,

        /// <summary>The first bytes are not a TLS handshake record (e.g. plain HTTP).</summary>
        NotTls,

        /// <summary>A ClientHello was parsed (SNI/ALPN may still be empty).</summary>
        Ok,
    }

    internal readonly record struct Result(ParseStatus Status, string? ServerName, IReadOnlyList<string> Alpn)
    {
        public bool OffersH2 => Alpn.Contains("h2");
        public bool OffersHttp11 => Alpn.Contains("http/1.1");

        /// <summary>
        /// True when the client offers <c>h2</c> with no <c>http/1.1</c> fallback. Such
        /// clients (notably gRPC) cannot be downgraded, so the proxy must blind-tunnel
        /// them or they break.
        /// </summary>
        public bool IsH2Only => OffersH2 && !OffersHttp11;
    }

    public static Result Parse(ReadOnlySequence<byte> sequence)
    {
        // ClientHellos are small; work on a contiguous copy capped to MaxScanBytes.
        var data = sequence.Length > MaxScanBytes ? sequence.Slice(0, MaxScanBytes).ToArray() : sequence.ToArray();
        var s = new ReadOnlySpan<byte>(data);

        if (s.Length < 5)
        {
            return new(ParseStatus.NeedMore, null, []);
        }
        if (s[0] != HandshakeRecordType)
        {
            return new(ParseStatus.NotTls, null, []);
        }

        var recordLength = BinaryPrimitives.ReadUInt16BigEndian(s.Slice(3, 2));
        if (s.Length < 5 + recordLength)
        {
            return new(ParseStatus.NeedMore, null, []);
        }

        var body = s.Slice(5, recordLength);
        if (body.Length < 4 || body[0] != ClientHelloType)
        {
            return new(ParseStatus.NotTls, null, []);
        }

        var handshakeLength = (body[1] << 16) | (body[2] << 8) | body[3];
        var p = 4;
        if (body.Length < p + handshakeLength)
        {
            return new(ParseStatus.NeedMore, null, []);
        }

        p += 2;  // client_version
        p += 32; // random
        if (p >= body.Length)
        {
            return new(ParseStatus.NeedMore, null, []);
        }

        int sessionIdLength = body[p];
        p += 1 + sessionIdLength;
        if (p + 2 > body.Length)
        {
            return new(ParseStatus.NeedMore, null, []);
        }

        var cipherSuitesLength = BinaryPrimitives.ReadUInt16BigEndian(body.Slice(p, 2));
        p += 2 + cipherSuitesLength;
        if (p >= body.Length)
        {
            return new(ParseStatus.NeedMore, null, []);
        }

        int compressionLength = body[p];
        p += 1 + compressionLength;
        if (p + 2 > body.Length)
        {
            // No extensions block — valid, but no SNI/ALPN to read.
            return new(ParseStatus.Ok, null, []);
        }

        var extensionsTotal = BinaryPrimitives.ReadUInt16BigEndian(body.Slice(p, 2));
        p += 2;

        string? serverName = null;
        var alpn = new List<string>();
        var extensionsEnd = Math.Min(body.Length, p + extensionsTotal);

        while (p + 4 <= extensionsEnd)
        {
            var extensionType = BinaryPrimitives.ReadUInt16BigEndian(body.Slice(p, 2));
            var extensionLength = BinaryPrimitives.ReadUInt16BigEndian(body.Slice(p + 2, 2));
            p += 4;
            if (p + extensionLength > body.Length)
            {
                break;
            }

            var extension = body.Slice(p, extensionLength);
            if (extensionType == ServerNameExtension)
            {
                serverName = ReadServerName(extension);
            }
            else if (extensionType == AlpnExtension)
            {
                ReadAlpn(extension, alpn);
            }

            p += extensionLength;
        }

        return new(ParseStatus.Ok, serverName, alpn);
    }

    private static string? ReadServerName(ReadOnlySpan<byte> extension)
    {
        // server_name_list: list_len(2)  name_type(1)=host_name(0)  name_len(2)  name
        if (extension.Length < 5 || extension[2] != 0x00)
        {
            return null;
        }

        var nameLength = BinaryPrimitives.ReadUInt16BigEndian(extension.Slice(3, 2));
        return extension.Length >= 5 + nameLength
            ? Encoding.ASCII.GetString(extension.Slice(5, nameLength))
            : null;
    }

    private static void ReadAlpn(ReadOnlySpan<byte> extension, List<string> alpn)
    {
        // ProtocolNameList: list_len(2)  [ proto_len(1) proto ]*
        if (extension.Length < 2)
        {
            return;
        }

        var listLength = BinaryPrimitives.ReadUInt16BigEndian(extension.Slice(0, 2));
        var q = 2;
        var end = Math.Min(extension.Length, 2 + listLength);
        while (q < end)
        {
            int protocolLength = extension[q];
            q += 1;
            if (q + protocolLength > extension.Length)
            {
                break;
            }
            alpn.Add(Encoding.ASCII.GetString(extension.Slice(q, protocolLength)));
            q += protocolLength;
        }
    }
}
