// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Abstractions.Proxy.Http;
using Xunit;

namespace DevProxy.Abstractions.Tests.Proxy.Http;

public class BodyModeResolverTests
{
    private static BodyContext Bounded(long? length, BodyDirection dir = BodyDirection.Response) =>
        new(dir, IsUpgrade: false, IsUnboundedStream: false, ContentLength: length);

    [Fact]
    public void Upgrade_AlwaysRaw_RegardlessOfCapabilities()
    {
        var ctx = new BodyContext(BodyDirection.Request, IsUpgrade: true, IsUnboundedStream: false, ContentLength: 10);
        var mode = BodyModeResolver.Resolve(BodyCapabilities.NeedsFullRequestBody | BodyCapabilities.CanMutate, ctx);
        Assert.Equal(BodyMode.UpgradedRaw, mode);
    }

    [Fact]
    public void NoBodyNeed_NoInspect_StreamsThrough()
    {
        var mode = BodyModeResolver.Resolve(BodyCapabilities.None, Bounded(10));
        Assert.Equal(BodyMode.StreamingPassThrough, mode);
    }

    [Fact]
    public void NoBodyNeed_WithInspect_Tees()
    {
        var mode = BodyModeResolver.Resolve(BodyCapabilities.CanStreamInspect, Bounded(10));
        Assert.Equal(BodyMode.TeeForInspection, mode);
    }

    [Fact]
    public void FullBody_SmallBounded_BuffersInMemory()
    {
        var mode = BodyModeResolver.Resolve(BodyCapabilities.NeedsFullResponseBody, Bounded(1024));
        Assert.Equal(BodyMode.BufferedInMemory, mode);
    }

    [Fact]
    public void Mutation_ImpliesFullBody_BuffersInMemory()
    {
        var mode = BodyModeResolver.Resolve(BodyCapabilities.CanMutate, Bounded(1024));
        Assert.Equal(BodyMode.BufferedInMemory, mode);
    }

    [Fact]
    public void FullBody_MediumBounded_SpoolsToDisk()
    {
        var length = BodyModeResolver.DefaultInMemoryLimitBytes + 1;
        var mode = BodyModeResolver.Resolve(BodyCapabilities.NeedsFullResponseBody, Bounded(length));
        Assert.Equal(BodyMode.SpooledToDisk, mode);
    }

    [Fact]
    public void FullBody_UnknownLengthButBounded_SpoolsDefensively()
    {
        var mode = BodyModeResolver.Resolve(BodyCapabilities.NeedsFullResponseBody, Bounded(null));
        Assert.Equal(BodyMode.SpooledToDisk, mode);
    }

    [Fact]
    public void FullBody_TooLargeToSpool_WithInspect_Tees()
    {
        var length = BodyModeResolver.DefaultSpoolLimitBytes + 1;
        var caps = BodyCapabilities.NeedsFullResponseBody | BodyCapabilities.CanStreamInspect;
        var mode = BodyModeResolver.Resolve(caps, Bounded(length));
        Assert.Equal(BodyMode.TeeForInspection, mode);
    }

    [Fact]
    public void FullBody_TooLargeToSpool_NoInspect_Streams()
    {
        var length = BodyModeResolver.DefaultSpoolLimitBytes + 1;
        var mode = BodyModeResolver.Resolve(BodyCapabilities.NeedsFullResponseBody, Bounded(length));
        Assert.Equal(BodyMode.StreamingPassThrough, mode);
    }

    [Fact]
    public void FullBody_UnboundedStream_NeverBuffers_DegradesToTee()
    {
        var ctx = new BodyContext(BodyDirection.Response, IsUpgrade: false, IsUnboundedStream: true, ContentLength: null);
        var caps = BodyCapabilities.NeedsFullResponseBody
            | BodyCapabilities.CannotRunOnInfiniteStreams
            | BodyCapabilities.CanStreamInspect;
        var mode = BodyModeResolver.Resolve(caps, ctx);
        Assert.Equal(BodyMode.TeeForInspection, mode);
    }

    [Fact]
    public void FullBody_UnboundedStream_NoInspect_Streams()
    {
        var ctx = new BodyContext(BodyDirection.Response, IsUpgrade: false, IsUnboundedStream: true, ContentLength: null);
        var mode = BodyModeResolver.Resolve(BodyCapabilities.NeedsFullResponseBody, ctx);
        Assert.Equal(BodyMode.StreamingPassThrough, mode);
    }

    [Fact]
    public void Direction_SelectsCorrectFlag()
    {
        // A plugin that needs the REQUEST body should not force buffering of the RESPONSE.
        var responseMode = BodyModeResolver.Resolve(
            BodyCapabilities.NeedsFullRequestBody, Bounded(1024, BodyDirection.Response));
        Assert.Equal(BodyMode.StreamingPassThrough, responseMode);

        var requestMode = BodyModeResolver.Resolve(
            BodyCapabilities.NeedsFullRequestBody, Bounded(1024, BodyDirection.Request));
        Assert.Equal(BodyMode.BufferedInMemory, requestMode);
    }
}
