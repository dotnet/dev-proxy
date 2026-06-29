using System.Buffers;
using System.Buffers.Binary;

namespace KestrelSpike;

/// <summary>
/// Minimal, tolerant TLS ClientHello parser. Extracts SNI (server name) and the ALPN
/// protocol list so the proxy can decide — BEFORE terminating TLS — whether to MITM
/// (advertise http/1.1) or blind-tunnel (h2-only / gRPC). Deliberately not a TLS stack.
/// </summary>
public static class TlsClientHello
{
    public enum ParseStatus { NeedMore, NotTls, Ok }

    public readonly record struct Result(ParseStatus Status, string? ServerName, IReadOnlyList<string> Alpn)
    {
        public bool OffersH2 => Alpn.Any(p => p == "h2");
        public bool OffersHttp11 => Alpn.Any(p => p == "http/1.1");
        // h2-only (no http/1.1 fallback) => must blind-tunnel or it breaks (gRPC).
        public bool IsH2Only => OffersH2 && !OffersHttp11;
    }

    public static Result Parse(ReadOnlySequence<byte> sequence)
    {
        // Work on a contiguous copy for simplicity (ClientHello is small).
        var data = sequence.Length > 8192 ? sequence.Slice(0, 8192).ToArray() : sequence.ToArray();
        var s = new ReadOnlySpan<byte>(data);

        if (s.Length < 5)
        {
            return new(ParseStatus.NeedMore, null, []);
        }
        // TLS record: handshake (0x16)
        if (s[0] != 0x16)
        {
            return new(ParseStatus.NotTls, null, []);
        }
        var recordLen = BinaryPrimitives.ReadUInt16BigEndian(s.Slice(3, 2));
        if (s.Length < 5 + recordLen)
        {
            return new(ParseStatus.NeedMore, null, []);
        }

        var body = s.Slice(5, recordLen);
        var p = 0;
        if (body.Length < 4 || body[p] != 0x01) // ClientHello
        {
            return new(ParseStatus.NotTls, null, []);
        }
        // handshake length (3 bytes)
        var hsLen = (body[1] << 16) | (body[2] << 8) | body[3];
        p = 4;
        if (body.Length < p + hsLen)
        {
            return new(ParseStatus.NeedMore, null, []);
        }

        p += 2;             // client_version
        p += 32;            // random
        if (p >= body.Length) return new(ParseStatus.NeedMore, null, []);
        int sidLen = body[p]; p += 1 + sidLen;                       // session id
        if (p + 2 > body.Length) return new(ParseStatus.NeedMore, null, []);
        int csLen = BinaryPrimitives.ReadUInt16BigEndian(body.Slice(p, 2)); p += 2 + csLen; // cipher suites
        if (p >= body.Length) return new(ParseStatus.NeedMore, null, []);
        int compLen = body[p]; p += 1 + compLen;                     // compression methods
        if (p + 2 > body.Length) return new(ParseStatus.Ok, null, []); // no extensions
        int extTotal = BinaryPrimitives.ReadUInt16BigEndian(body.Slice(p, 2)); p += 2;

        string? sni = null;
        var alpn = new List<string>();
        var extEnd = Math.Min(body.Length, p + extTotal);

        while (p + 4 <= extEnd)
        {
            var extType = BinaryPrimitives.ReadUInt16BigEndian(body.Slice(p, 2));
            var extLen = BinaryPrimitives.ReadUInt16BigEndian(body.Slice(p + 2, 2));
            p += 4;
            if (p + extLen > body.Length) break;
            var ext = body.Slice(p, extLen);

            if (extType == 0x0000) // server_name
            {
                // list len(2), type(1)=host_name(0), name len(2), name
                if (ext.Length >= 5 && ext[2] == 0x00)
                {
                    var nameLen = BinaryPrimitives.ReadUInt16BigEndian(ext.Slice(3, 2));
                    if (ext.Length >= 5 + nameLen)
                    {
                        sni = System.Text.Encoding.ASCII.GetString(ext.Slice(5, nameLen));
                    }
                }
            }
            else if (extType == 0x0010) // ALPN
            {
                // list len(2), then [len(1) proto]...
                if (ext.Length >= 2)
                {
                    var listLen = BinaryPrimitives.ReadUInt16BigEndian(ext.Slice(0, 2));
                    var q = 2;
                    var end = Math.Min(ext.Length, 2 + listLen);
                    while (q < end)
                    {
                        int protoLen = ext[q]; q += 1;
                        if (q + protoLen > ext.Length) break;
                        alpn.Add(System.Text.Encoding.ASCII.GetString(ext.Slice(q, protoLen)));
                        q += protoLen;
                    }
                }
            }
            p += extLen;
        }

        return new(ParseStatus.Ok, sni, alpn);
    }
}
