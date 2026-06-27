// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Net;

namespace DevProxy.Abstractions.Proxy.Http;

/// <summary>
/// An intercepted (or mocked) HTTP response in the canonical Dev Proxy model.
/// </summary>
public interface IHttpResponse : IHttpMessage
{
    /// <summary>HTTP status code.</summary>
    HttpStatusCode StatusCode { get; set; }

    /// <summary>
    /// Reason phrase. When <c>null</c> the engine emits the default phrase for
    /// <see cref="StatusCode"/>.
    /// </summary>
    string? StatusDescription { get; set; }
}
