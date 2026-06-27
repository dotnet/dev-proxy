// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections;
using DevProxy.Abstractions.Proxy.Http;
using TitaniumHeaders = Titanium.Web.Proxy.Http.HeaderCollection;

namespace DevProxy.Proxy.Titanium;

/// <summary>
/// Projects a Titanium <see cref="TitaniumHeaders"/> onto the canonical
/// <see cref="IHeaderCollection"/>. Reads and writes operate directly on the
/// underlying Titanium collection so mutations are visible to the engine.
/// </summary>
public sealed class TitaniumHeaderCollection : IHeaderCollection
{
    private readonly TitaniumHeaders _headers;

    /// <summary>Wraps an existing Titanium header collection.</summary>
    public TitaniumHeaderCollection(TitaniumHeaders headers)
    {
        ArgumentNullException.ThrowIfNull(headers);
        _headers = headers;
    }

    /// <inheritdoc />
    public int Count => _headers.GetAllHeaders().Count;

    /// <inheritdoc />
    public bool Contains(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return _headers.HeaderExists(name);
    }

    /// <inheritdoc />
    public IHttpHeader? GetFirst(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var header = _headers.GetFirstHeader(name);
        return header is null ? null : new HttpHeader(header.Name, header.Value);
    }

    /// <inheritdoc />
    public IEnumerable<IHttpHeader> GetAll(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var headers = _headers.GetHeaders(name);
        return headers is null
            ? []
            : headers.Select(h => (IHttpHeader)new HttpHeader(h.Name, h.Value));
    }

    /// <inheritdoc />
    public void Add(string name, string value)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(value);
        _headers.AddHeader(name, value);
    }

    /// <inheritdoc />
    public void Add(IHttpHeader header)
    {
        ArgumentNullException.ThrowIfNull(header);
        _headers.AddHeader(header.Name, header.Value);
    }

    /// <inheritdoc />
    public void AddRange(IEnumerable<IHttpHeader> headers)
    {
        ArgumentNullException.ThrowIfNull(headers);
        foreach (var header in headers)
        {
            _headers.AddHeader(header.Name, header.Value);
        }
    }

    /// <inheritdoc />
    public void Replace(string name, string value)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(value);
        _ = _headers.RemoveHeader(name);
        _headers.AddHeader(name, value);
    }

    /// <inheritdoc />
    public bool Remove(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return _headers.RemoveHeader(name);
    }

    /// <inheritdoc />
    public IEnumerator<IHttpHeader> GetEnumerator() =>
        _headers.GetAllHeaders()
            .Select(h => (IHttpHeader)new HttpHeader(h.Name, h.Value))
            .GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
