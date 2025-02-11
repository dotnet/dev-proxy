// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Net.Http.Headers;
using Azure.Core;

namespace Microsoft.DevProxy.Plugins;

internal class AuthenticationDelegatingHandler(TokenCredential credential, string[] scopes) : DelegatingHandler
{
    private readonly TokenCredential _credential = credential;
    private readonly string[] _scopes = scopes;
    private DateTimeOffset? _expiresOn;
    private string? _accessToken;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var accessToken = await GetAccessTokenAsync(cancellationToken);
        
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        return await base.SendAsync(request, cancellationToken);
    }

    public async Task<string?> GetAccessTokenAsync(CancellationToken cancellationToken)
    {
        if (_expiresOn is null || _expiresOn < DateTimeOffset.UtcNow)
        {
            var accessToken = await _credential.GetTokenAsync(new TokenRequestContext(_scopes), cancellationToken);
            _expiresOn = accessToken.ExpiresOn;
            _accessToken = accessToken.Token;
        }

        return _accessToken;
    }
}
