// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Azure.Core;

namespace DevProxy.Plugins.Handlers;

internal sealed class AuthenticationDelegatingHandler(TokenCredential credential, string[] scopes) : DelegatingHandler
{
    private string? _accessToken;
    private readonly TokenCredential _credential = credential;
    private DateTimeOffset? _expiresOn;
    private readonly string[] _scopes = scopes;

    public async Task<string?> GetAccessTokenAsync(CancellationToken cancellationToken)
    {
        if (_expiresOn is null || _expiresOn < DateTimeOffset.UtcNow)
        {
            var accessToken = await _credential.GetTokenAsync(new(_scopes), cancellationToken);
            _expiresOn = accessToken.ExpiresOn;
            _accessToken = accessToken.Token;
        }

        return _accessToken;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var accessToken = await GetAccessTokenAsync(cancellationToken);

        request.Headers.Authorization = new("Bearer", accessToken);

        return await base.SendAsync(request, cancellationToken);
    }
}
