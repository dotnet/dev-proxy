// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

namespace DevProxy.Abstractions.Proxy.Http;

/// <summary>
/// A single HTTP header. Canonical Dev Proxy model — independent of any
/// underlying proxy engine. Immutable: to change a header, replace it in the
/// owning <see cref="IHeaderCollection"/>.
/// </summary>
public interface IHttpHeader
{
    /// <summary>Header name (case-insensitive per RFC 9110).</summary>
    string Name { get; }

    /// <summary>Header value.</summary>
    string Value { get; }
}
