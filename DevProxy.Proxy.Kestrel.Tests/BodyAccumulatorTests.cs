// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Proxy.Kestrel.Internal;

using Xunit;

namespace DevProxy.Proxy.Kestrel.Tests;

public class BodyAccumulatorTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(0, 200_000)]
    [InlineData(1000, 1000)]
    [InlineData(1000, 200_000)]
    [InlineData(200_000, 1000)]
    [InlineData(200_000, 200_000)]
    public void ToArray_ReturnsWrittenBytes_RegardlessOfExpectedLength(int expectedLength, int actualLength)
    {
        var data = new byte[actualLength];
        for (var i = 0; i < data.Length; i++)
        {
            data[i] = (byte)(i % 251);
        }
        using var accumulator = new BodyAccumulator(expectedLength);

        // Mix Write and GetMemory/Advance in uneven pieces so writes straddle segments.
        var offset = 0;
        var useWrite = true;
        while (offset < data.Length)
        {
            var count = Math.Min(7919, data.Length - offset);
            if (useWrite)
            {
                accumulator.Write(data.AsSpan(offset, count));
            }
            else
            {
                var target = accumulator.GetMemory();
                count = Math.Min(count, target.Length);
                data.AsSpan(offset, count).CopyTo(target.Span);
                accumulator.Advance(count);
            }
            offset += count;
            useWrite = !useWrite;
        }

        Assert.Equal(actualLength, accumulator.Length);
        Assert.Equal(data, accumulator.ToArray());
    }

    [Fact]
    public void Advance_Throws_PastEndOfCurrentSegment()
    {
        using var accumulator = new BodyAccumulator(10);
        _ = accumulator.GetMemory();

        _ = Assert.Throws<InvalidOperationException>(() => accumulator.Advance(11));
    }
}
