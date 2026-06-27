// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections.Concurrent;
using System.Globalization;
using DevProxy.Abstractions.Plugins;
using DevProxy.Abstractions.Proxy;
using DevProxy.Proxy.Kestrel.Http;
using Microsoft.Extensions.Logging;

namespace DevProxy.Proxy.Kestrel.Internal;

/// <summary>Outcome of the request phase, telling the engine how to proceed.</summary>
internal enum RequestPhase
{
    /// <summary>URL/headers not watched: forward upstream untouched, no response-phase plugins.</summary>
    NotWatched,

    /// <summary>Watched and no plugin produced a response: forward upstream, then run response plugins.</summary>
    Watched,

    /// <summary>A plugin produced a mock response: skip upstream, write the response directly.</summary>
    Mocked,
}

/// <summary>
/// Runs the Dev Proxy plugin lifecycle (<c>BeforeRequest</c> → <c>BeforeResponse</c>
/// → <c>AfterResponse</c>) against the canonical model for the Kestrel engine.
/// This mirrors the request/response handling that the Titanium-bound
/// <c>ProxyEngine</c> performs today; at cut-over it becomes the single pipeline.
///
/// <para>
/// Per-exchange plugin state is keyed on <see cref="Abstractions.Proxy.Http.IProxySession.SessionId"/>
/// (stable across request and response phases of the same exchange) — never on
/// object identity — so reusing a connection cannot leak state between exchanges.
/// </para>
/// </summary>
internal sealed class PluginPipeline
{
    private readonly IEnumerable<IPlugin> _plugins;
    private readonly ISet<UrlToWatch> _urlsToWatch;
    private readonly IProxyConfiguration _config;
    private readonly ILogger _logger;
    private readonly Dictionary<string, object> _globalData;
    private readonly HostWatchList _hosts;
    private readonly ConcurrentDictionary<string, Dictionary<string, object>> _sessionData = new(StringComparer.Ordinal);

    public PluginPipeline(
        IEnumerable<IPlugin> plugins,
        ISet<UrlToWatch> urlsToWatch,
        IProxyConfiguration config,
        Dictionary<string, object> globalData,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(plugins);
        ArgumentNullException.ThrowIfNull(urlsToWatch);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(globalData);
        ArgumentNullException.ThrowIfNull(logger);

        _plugins = plugins;
        _urlsToWatch = urlsToWatch;
        _config = config;
        _globalData = globalData;
        _logger = logger;
        _hosts = HostWatchList.FromUrls(urlsToWatch);
    }

    /// <summary>
    /// True when a CONNECT to this host should be intercepted (TLS terminated). A
    /// non-watched host is blind-tunnelled byte-for-byte.
    /// </summary>
    public bool IsProxiedHost(string host) => _hosts.IsWatched(host);

    public async Task<RequestPhase> RunRequestAsync(CanonicalProxySession session, CancellationToken ct)
    {
        var request = session.Request;
        if (!IsProxiedHost(request.RequestUri.Host) || !IsIncludedByHeaders(request))
        {
            return RequestPhase.NotWatched;
        }

        var responseState = new ResponseState();
        var sessionData = _sessionData.GetOrAdd(session.SessionId, static _ => []);
        var args = new ProxyRequestArgs(session, responseState)
        {
            SessionData = sessionData,
            GlobalData = _globalData,
        };

        if (!args.HasRequestUrlMatch(_urlsToWatch))
        {
            _ = _sessionData.TryRemove(session.SessionId, out _);
            return RequestPhase.NotWatched;
        }

        var loggingContext = new LoggingContext(session);

        // Open the requestId scope for the whole request phase so that plugin logs
        // (emitted under the plugin's own logger category) are grouped/flushed with
        // the engine's request-lifecycle lines by the console formatter — mirroring
        // the Titanium engine's BeforeRequest scope.
        using (BeginRequestScope(session))
        {
            _logger.LogRequest($"{request.Method} {request.Url}", MessageType.InterceptedRequest, loggingContext);
            _logger.LogRequest(DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture), MessageType.Timestamp, loggingContext);

            foreach (var plugin in _plugins.Where(p => p.Enabled))
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    await plugin.BeforeRequestAsync(args, ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "An error occurred in a plugin");
                }
            }

            if (responseState.HasBeenSet || session.HasResponse)
            {
                return RequestPhase.Mocked;
            }

            _logger.LogRequest("Passed through", MessageType.PassedThrough, loggingContext);
        }
        return RequestPhase.Watched;
    }

    public async Task RunResponseAsync(CanonicalProxySession session, CancellationToken ct)
    {
        if (!_sessionData.TryGetValue(session.SessionId, out var sessionData))
        {
            sessionData = [];
        }

        var beforeArgs = new ProxyResponseArgs(session, new ResponseState())
        {
            SessionData = sessionData,
            GlobalData = _globalData,
        };

        var loggingContext = new LoggingContext(session);
        var message = $"{session.Request.Method} {session.Request.Url}";

        try
        {
            // Single requestId scope across the whole response phase so plugin logs
            // (BeforeResponse/AfterResponse) are grouped with the engine's lines and
            // flushed together on FinishedProcessingRequest.
            using (BeginRequestScope(session))
            {
                foreach (var plugin in _plugins.Where(p => p.Enabled))
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        await plugin.BeforeResponseAsync(beforeArgs, ct).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "An error occurred in a plugin");
                    }
                }

                _logger.LogRequest(message, MessageType.InterceptedResponse, loggingContext);

                foreach (var plugin in _plugins.Where(p => p.Enabled))
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        await plugin.AfterResponseAsync(beforeArgs, ct).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "An error occurred in a plugin");
                    }
                }

                _logger.LogRequest(message, MessageType.FinishedProcessingRequest, loggingContext);
            }
        }
        finally
        {
            _ = _sessionData.TryRemove(session.SessionId, out _);
        }
    }

    // The console formatter buffers request-log lines by an integer "requestId" scope
    // and flushes the group on FinishedProcessingRequest. Mirror the Titanium engine by
    // opening that scope (method + url + stable RequestId) around every request-log emit.
    private IDisposable? BeginRequestScope(CanonicalProxySession session) =>
        _logger.BeginScope(new Dictionary<string, object>
        {
            ["method"] = session.Request.Method,
            ["url"] = session.Request.Url,
            ["requestId"] = session.RequestId,
        });

    /// <summary>Drops any per-exchange state for a session that never reached the response phase.</summary>
    public void Forget(string sessionId) => _sessionData.TryRemove(sessionId, out _);

    private bool IsIncludedByHeaders(Abstractions.Proxy.Http.IHttpRequest request)
    {
        if (_config.FilterByHeaders is null)
        {
            return true;
        }

        foreach (var header in _config.FilterByHeaders)
        {
            if (request.Headers.Contains(header.Name))
            {
                if (string.IsNullOrEmpty(header.Value))
                {
                    return true;
                }

                if (request.Headers.GetAll(header.Name)
                    .Any(h => h.Value.Contains(header.Value, StringComparison.OrdinalIgnoreCase)))
                {
                    return true;
                }
            }
        }

        return false;
    }
}
