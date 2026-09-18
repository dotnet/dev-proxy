// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Buffers;
using System.IO.Pipelines;

namespace DevProxy.Proxy.Kestrel.Internal;

/// <summary>
/// Adapts an <see cref="IDuplexPipe"/> (Kestrel's connection transport) to a
/// <see cref="Stream"/> so it can be wrapped by <c>SslStream</c> and read/written
/// with the usual stream helpers.
/// </summary>
internal sealed class DuplexPipeStream(IDuplexPipe pipe) : Stream
{
    private readonly PipeReader _input = pipe.Input;
    private readonly PipeWriter _output = pipe.Output;

    public override bool CanRead => true;
    public override bool CanWrite => true;
    public override bool CanSeek => false;
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        while (true)
        {
            var result = await _input.ReadAsync(cancellationToken).ConfigureAwait(false);
            var sequence = result.Buffer;

            if (sequence.Length > 0)
            {
                var toCopy = (int)Math.Min(sequence.Length, buffer.Length);
                sequence.Slice(0, toCopy).CopyTo(buffer.Span);
                _input.AdvanceTo(sequence.GetPosition(toCopy));
                return toCopy;
            }

            if (result.IsCompleted)
            {
                _input.AdvanceTo(sequence.End);
                return 0;
            }

            _input.AdvanceTo(sequence.Start, sequence.End);
        }
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
        _ = await _output.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);

    public override int Read(byte[] buffer, int offset, int count) =>
        ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override void Write(byte[] buffer, int offset, int count) =>
        WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override void Flush() { }

    public override Task FlushAsync(CancellationToken cancellationToken) =>
        _output.FlushAsync(cancellationToken).AsTask();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}
