// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections;

namespace DevProxy.Abstractions.Proxy.Http;

/// <summary>
/// Default in-memory <see cref="IHeaderCollection"/>. Preserves insertion (wire)
/// order and matches names case-insensitively. Used by adapters, mocked
/// responses, and tests.
/// </summary>
public sealed class HeaderCollection : IHeaderCollection
{
    private readonly List<IHttpHeader> _headers;

    /// <summary>Creates an empty collection.</summary>
    public HeaderCollection() => _headers = [];

    /// <summary>Creates a collection seeded with the given headers (wire order preserved).</summary>
    public HeaderCollection(IEnumerable<IHttpHeader> headers)
    {
        ArgumentNullException.ThrowIfNull(headers);
        _headers = [.. headers];
    }

    /// <inheritdoc />
    public int Count => _headers.Count;

    /// <inheritdoc />
    public bool Contains(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return _headers.Exists(h => NameEquals(h.Name, name));
    }

    /// <inheritdoc />
    public IHttpHeader? GetFirst(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return _headers.Find(h => NameEquals(h.Name, name));
    }

    /// <inheritdoc />
    public IEnumerable<IHttpHeader> GetAll(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return _headers.Where(h => NameEquals(h.Name, name));
    }

    /// <inheritdoc />
    public void Add(string name, string value)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(value);
        _headers.Add(new HttpHeader(name, value));
    }

    /// <inheritdoc />
    public void Add(IHttpHeader header)
    {
        ArgumentNullException.ThrowIfNull(header);
        _headers.Add(header);
    }

    /// <inheritdoc />
    public void AddRange(IEnumerable<IHttpHeader> headers)
    {
        ArgumentNullException.ThrowIfNull(headers);
        _headers.AddRange(headers);
    }

    /// <inheritdoc />
    public void Replace(string name, string value)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(value);
        _ = Remove(name);
        _headers.Add(new HttpHeader(name, value));
    }

    /// <inheritdoc />
    public bool Remove(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return _headers.RemoveAll(h => NameEquals(h.Name, name)) > 0;
    }

    /// <inheritdoc />
    public IEnumerator<IHttpHeader> GetEnumerator() => _headers.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    private static bool NameEquals(string a, string b) =>
        string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
