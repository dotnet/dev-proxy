// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Microsoft.Extensions.Logging;
using System.Globalization;

namespace DevProxy.Plugins.Handlers;

internal sealed class TracingDelegatingHandler(ILogger logger) : DelegatingHandler
{
    private readonly ILogger _logger = logger;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using var scope = _logger.BeginScope(request.GetHashCode().ToString(CultureInfo.InvariantCulture));

        _logger.LogTrace("Request: {Method} {Uri}", request.Method, request.RequestUri);
        foreach (var (header, value) in request.Headers)
        {
            _logger.LogTrace("{Header}: {Value}", header, string.Join(", ", value));
        }
        if (request.Content is not null)
        {
            var body = await request.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogTrace("Body: {Body}", body);
        }

        var response = await base.SendAsync(request, cancellationToken);

        _logger.LogTrace("Response");
        foreach (var (header, value) in response.Headers)
        {
            _logger.LogTrace("{Header}: {Value}", header, string.Join(", ", value));
        }
        if (response.Content is not null)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogTrace("Body: {Body}", body);
        }

        return response;
    }
}