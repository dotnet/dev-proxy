// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Abstractions.Proxy.Http;
using TitaniumRequest = Titanium.Web.Proxy.Http.Request;

namespace DevProxy.Proxy.Titanium;

/// <summary>
/// Projects a Titanium <see cref="TitaniumRequest"/> onto the canonical
/// <see cref="IHttpRequest"/>.
/// </summary>
public sealed class TitaniumRequestAdapter : TitaniumHttpMessageAdapter, IHttpRequest
{
    private readonly TitaniumRequest _request;

    /// <param name="request">The Titanium request to wrap.</param>
    /// <param name="setBody">
    /// Optional body setter (typically the session's <c>SetRequestBody</c>) that
    /// keeps Titanium's request state consistent on mutation.
    /// </param>
    public TitaniumRequestAdapter(TitaniumRequest request, Action<byte[]>? setBody = null)
        : base(request, setBody)
    {
        _request = request;
    }

    /// <inheritdoc />
    public Uri RequestUri => _request.RequestUri!;

    /// <inheritdoc />
    public string Url
    {
        get => _request.Url;
        set => _request.Url = value;
    }

    /// <inheritdoc />
    public string Method => _request.Method!;

    /// <inheritdoc />
    public Version HttpVersion => _request.HttpVersion;

    /// <inheritdoc />
    public bool IsWebSocketRequest => _request.UpgradeToWebSocket;
}
