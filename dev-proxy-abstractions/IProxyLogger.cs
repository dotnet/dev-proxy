// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Titanium.Web.Proxy.EventArguments;

namespace Microsoft.DevProxy.Abstractions;

public enum MessageType
{
    Normal,
    InterceptedRequest,
    PassedThrough,
    Warning,
    Tip,
    Failed,
    Chaos,
    Mocked,
    InterceptedResponse,
    FinishedProcessingRequest,
    Skipped,
    Processed,
    Timestamp
}

public class LoggingContext(SessionEventArgs session)
{
    public SessionEventArgs Session { get; } = session;
}