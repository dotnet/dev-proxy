// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

namespace DevProxy.Abstractions.Proxy.Http;

/// <summary>
/// An ordered, case-insensitive collection of HTTP headers. Implements
/// <see cref="IEnumerable{T}"/> so LINQ (<c>FirstOrDefault</c>, <c>Any</c>,
/// <c>Select</c>, <c>Where</c>) works directly against it.
///
/// <para>
/// Header names are matched case-insensitively (RFC 9110). A name may appear
/// more than once (e.g. <c>Set-Cookie</c>); <see cref="GetAll"/> returns every
/// occurrence in wire order while <see cref="GetFirst"/> returns the first.
/// </para>
/// </summary>
public interface IHeaderCollection : IEnumerable<IHttpHeader>
{
    /// <summary>Number of header entries (counts repeated names separately).</summary>
    int Count { get; }

    /// <summary>True if at least one header with the given name exists.</summary>
    bool Contains(string name);

    /// <summary>First header with the given name, or <c>null</c>.</summary>
    IHttpHeader? GetFirst(string name);

    /// <summary>All headers with the given name, in wire order (never null).</summary>
    IEnumerable<IHttpHeader> GetAll(string name);

    /// <summary>Appends a header. Does not remove existing headers with the same name.</summary>
    void Add(string name, string value);

    /// <summary>Appends a header. Does not remove existing headers with the same name.</summary>
    void Add(IHttpHeader header);

    /// <summary>Appends a range of headers.</summary>
    void AddRange(IEnumerable<IHttpHeader> headers);

    /// <summary>
    /// Replaces all headers with the given name with a single header of that
    /// name and value, adding it if none existed.
    /// </summary>
    void Replace(string name, string value);

    /// <summary>Removes all headers with the given name. Returns true if any were removed.</summary>
    bool Remove(string name);
}
