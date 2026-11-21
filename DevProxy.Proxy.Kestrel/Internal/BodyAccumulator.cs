// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Buffers;

namespace DevProxy.Proxy.Kestrel.Internal;

/// <summary>
/// Accumulates a body of unknown length into a list of pooled segments, then
/// materializes it with a single exact-size copy. Unlike a growing
/// <see cref="MemoryStream"/> (which reallocates and copies on every doubling and
/// then copies once more in <c>ToArray()</c>), each byte is copied exactly once on
/// the way in and once on the way out, and the segments stay below the large
/// object heap threshold.
///
/// <para>
/// When the expected length is known up front, pass it as
/// <c>expectedLength</c>: the first segment is then allocated at exactly that size
/// and <see cref="ToArray"/> returns it without copying if the body matches.
/// </para>
///
/// <para>Enforcing a size cap is the caller's job: check <see cref="Length"/>
/// before calling <see cref="Advance"/>.</para>
/// </summary>
internal sealed class BodyAccumulator(int expectedLength = 0) : IDisposable
{
    private const int SegmentBytes = 64 * 1024;

    private readonly List<byte[]> _segments = [];
    private byte[]? _owned;
    private int _lastUsed;
    private bool _disposed;

    public int Length { get; private set; }

    /// <summary>Returns writable space at the end of the body; call <see cref="Advance"/> after filling it.</summary>
    public Memory<byte> GetMemory()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_segments.Count == 0 || _lastUsed == _segments[^1].Length)
        {
            byte[] segment;
            if (_segments.Count == 0 && expectedLength > 0)
            {
                segment = _owned = new byte[expectedLength];
            }
            else
            {
                segment = ArrayPool<byte>.Shared.Rent(SegmentBytes);
            }
            _segments.Add(segment);
            _lastUsed = 0;
        }

        return _segments[^1].AsMemory(_lastUsed);
    }

    public void Advance(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (count == 0)
        {
            return;
        }
        if (_segments.Count == 0 || count > _segments[^1].Length - _lastUsed)
        {
            throw new InvalidOperationException("Cannot advance past the end of the current segment.");
        }
        _lastUsed += count;
        Length += count;
    }

    public void Write(ReadOnlySpan<byte> data)
    {
        while (!data.IsEmpty)
        {
            var target = GetMemory().Span;
            var count = Math.Min(target.Length, data.Length);
            data[..count].CopyTo(target);
            Advance(count);
            data = data[count..];
        }
    }

    public byte[] ToArray()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (Length == 0)
        {
            return [];
        }
        if (_segments.Count == 1 && ReferenceEquals(_segments[0], _owned) && Length == _owned!.Length)
        {
            return _owned;
        }

        var result = new byte[Length];
        var offset = 0;
        for (var i = 0; i < _segments.Count; i++)
        {
            var count = i == _segments.Count - 1 ? _lastUsed : _segments[i].Length;
            _segments[i].AsSpan(0, count).CopyTo(result.AsSpan(offset));
            offset += count;
        }
        return result;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        foreach (var segment in _segments)
        {
            if (!ReferenceEquals(segment, _owned))
            {
                ArrayPool<byte>.Shared.Return(segment);
            }
        }
        _segments.Clear();
    }
}
