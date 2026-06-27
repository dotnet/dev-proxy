// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Net;
using DevProxy.Abstractions.Proxy.Http;
using TitaniumResponse = Titanium.Web.Proxy.Http.Response;

namespace DevProxy.Proxy.Titanium;

/// <summary>
/// Projects a Titanium <see cref="TitaniumResponse"/> onto the canonical
/// <see cref="IHttpResponse"/>. Titanium stores the status as an <see cref="int"/>;
/// the canonical model exposes it as a strongly-typed <see cref="HttpStatusCode"/>.
/// </summary>
public sealed class TitaniumResponseAdapter : TitaniumHttpMessageAdapter, IHttpResponse
{
    private readonly TitaniumResponse _response;

    /// <param name="response">The Titanium response to wrap.</param>
    /// <param name="setBody">
    /// Optional body setter (typically the session's <c>SetResponseBody</c>) that
    /// keeps Titanium's response state consistent on mutation.
    /// </param>
    public TitaniumResponseAdapter(TitaniumResponse response, Action<byte[]>? setBody = null)
        : base(response, setBody)
    {
        _response = response;
    }

    /// <inheritdoc />
    public HttpStatusCode StatusCode
    {
        get => (HttpStatusCode)_response.StatusCode;
        set => _response.StatusCode = (int)value;
    }

    /// <inheritdoc />
    public string? StatusDescription
    {
        get => _response.StatusDescription;
        set => _response.StatusDescription = value ?? string.Empty;
    }
}
