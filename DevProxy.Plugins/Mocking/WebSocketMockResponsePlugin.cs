// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Abstractions.Plugins;
using DevProxy.Abstractions.Proxy;
using DevProxy.Abstractions.Proxy.Http;
using DevProxy.Abstractions.Utils;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.CommandLine;
using System.CommandLine.Parsing;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using FrameworkWsMessageType = System.Net.WebSockets.WebSocketMessageType;

namespace DevProxy.Plugins.Mocking;

public sealed class WebSocketMockResponseConfiguration
{
    public IEnumerable<WebSocketMock> Mocks { get; set; } = [];

    [JsonIgnore]
    public string MocksFile { get; set; } = "websocket-mocks.json";

    [JsonIgnore]
    public bool NoMocks { get; set; }

    [JsonPropertyName("$schema")]
    public string Schema { get; set; } = "https://raw.githubusercontent.com/dotnet/dev-proxy/main/schemas/v3.1.0/websocketmockresponseplugin.mocksfile.schema.json";
}

/// <summary>
/// Mocks WebSocket conversations: matched <c>ws://</c>/<c>wss://</c> upgrades are answered
/// by the proxy itself (the origin is never contacted) and a scripted exchange runs over
/// the connection. This is the WebSocket analogue of <see cref="MockResponsePlugin"/>.
///
/// <code>
///   BeforeRequest: is this a watched WebSocket upgrade with a matching mock?
///        │ yes
///        ▼
///   session.HandleWebSocket(handler)   ── engine completes the handshake, then runs:
///        │
///        ├─ send each OnConnect message
///        └─ loop: receive client message → first matching Rule → send Responses
///                                         (no match → CloseOnUnmatched ? close : ignore)
/// </code>
/// </summary>
public sealed class WebSocketMockResponsePlugin(
    HttpClient httpClient,
    ILogger<WebSocketMockResponsePlugin> logger,
    ISet<UrlToWatch> urlsToWatch,
    IProxyConfiguration proxyConfiguration,
    IConfigurationSection pluginConfigurationSection) :
    BasePlugin<WebSocketMockResponseConfiguration>(
        httpClient,
        logger,
        urlsToWatch,
        proxyConfiguration,
        pluginConfigurationSection)
{
    private const string _noMocksOptionName = "--no-websocket-mocks";
    private const string _mocksFileOptionName = "--websocket-mocks-file";

    private readonly IProxyConfiguration _proxyConfiguration = proxyConfiguration;

    private WebSocketMockResponsesLoader? _loader;

    public override string Name => nameof(WebSocketMockResponsePlugin);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _loader?.Dispose();
        }
        base.Dispose(disposing);
    }

    public override async Task InitializeAsync(InitArgs e, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(e);

        await base.InitializeAsync(e, cancellationToken);

        _loader = ActivatorUtilities.CreateInstance<WebSocketMockResponsesLoader>(e.ServiceProvider, Configuration);
    }

    public override System.CommandLine.Option[] GetOptions()
    {
        var noMocks = new System.CommandLine.Option<bool?>(_noMocksOptionName)
        {
            Description = "Disable loading WebSocket mock responses",
            HelpName = "no-websocket-mocks"
        };

        var mocksFile = new System.CommandLine.Option<string?>(_mocksFileOptionName)
        {
            Description = "Provide a file populated with WebSocket mock responses",
            HelpName = "websocket-mocks-file"
        };

        return [noMocks, mocksFile];
    }

    public override void OptionsLoaded(OptionsLoadedArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);

        base.OptionsLoaded(e);

        var parseResult = e.ParseResult;

        var noMocks = parseResult.GetValueOrDefault<bool?>(_noMocksOptionName);
        if (noMocks.HasValue)
        {
            Configuration.NoMocks = noMocks.Value;
        }

        var mocksFile = parseResult.GetValueOrDefault<string?>(_mocksFileOptionName);
        if (mocksFile is not null)
        {
            Configuration.MocksFile = mocksFile;
        }

        Configuration.MocksFile = ProxyUtils.GetFullPath(Configuration.MocksFile, _proxyConfiguration.ConfigFile);
        _loader!.InitFileWatcherAsync(CancellationToken.None).GetAwaiter().GetResult();
    }

    public override Task BeforeRequestAsync(ProxyRequestArgs e, CancellationToken cancellationToken)
    {
        Logger.LogTrace("{Method} called", nameof(BeforeRequestAsync));

        ArgumentNullException.ThrowIfNull(e);

        var request = e.ProxySession.Request;

        // Only WebSocket upgrades are in scope; everything else falls through to the
        // normal HTTP pipeline (other plugins, origin forwarding).
        if (!request.IsWebSocketRequest)
        {
            return Task.CompletedTask;
        }

        if (Configuration.NoMocks)
        {
            Logger.LogRequest("WebSocket mocks disabled", MessageType.Skipped, new LoggingContext(e.ProxySession));
            return Task.CompletedTask;
        }

        if (!e.ShouldExecute(UrlsToWatch))
        {
            Logger.LogRequest("URL not matched", MessageType.Skipped, new LoggingContext(e.ProxySession));
            return Task.CompletedTask;
        }

        var mock = GetMatchingMock(request);
        if (mock is null)
        {
            Logger.LogRequest("No matching WebSocket mock found", MessageType.Skipped, new LoggingContext(e.ProxySession));
            return Task.CompletedTask;
        }

        // Clone so concurrent connections don't share/mutate the same instance.
        var scripted = (WebSocketMock)mock.Clone();
        // NOTE: do NOT set ResponseState.HasBeenSet here. The mock is served over the
        // WebSocket transport, not as an HTTP response — leaving the session in the
        // Watched phase (no HTTP response) lets the engine's IsWebSocketRequest branch
        // dispatch to the WebSocketMockResponder. Setting HasBeenSet would short-circuit
        // the pipeline into the Mocked/ResponseWriter path and corrupt the handshake.
        e.ProxySession.HandleWebSocket((connection, ct) => RunMockAsync(scripted, connection, ct));

        Logger.LogRequest($"Mocking WebSocket {request.Url}", MessageType.Mocked, new LoggingContext(e.ProxySession));

        Logger.LogTrace("Left {Name}", nameof(BeforeRequestAsync));
        return Task.CompletedTask;
    }

    // ── mock conversation pump ──────────────────────────────────────────────
    //
    //   send OnConnect messages
    //   while open:
    //     receive client message
    //       └─ first Rule whose Match matches → send Responses (+ optional close)
    //          no match → CloseOnUnmatched ? close : keep listening
    private static async Task RunMockAsync(WebSocketMock mock, IWebSocketConnection connection, CancellationToken ct)
    {
        foreach (var message in mock.OnConnect)
        {
            await SendAsync(connection, message, ct).ConfigureAwait(false);
        }

        while (!ct.IsCancellationRequested)
        {
            var received = await connection.ReceiveAsync(ct).ConfigureAwait(false);
            if (received is null || received.Type == FrameworkWsMessageType.Close)
            {
                break;
            }

            var text = received.Text;
            var rule = mock.Rules.FirstOrDefault(r => WebSocketMessageMatcher.Matches(r.Match, text));
            if (rule is null)
            {
                if (mock.CloseOnUnmatched)
                {
                    break;
                }
                continue;
            }

            foreach (var response in rule.Responses)
            {
                await SendAsync(connection, response, ct).ConfigureAwait(false);
            }

            if (rule.CloseAfter)
            {
                break;
            }
        }
    }

    private static Task SendAsync(IWebSocketConnection connection, WebSocketMessageMock message, CancellationToken ct)
    {
        var body = message.Body ?? string.Empty;
        return message.MessageType == WebSocketMessageType.Binary
            ? connection.SendBinaryAsync(Convert.FromBase64String(body), ct)
            : connection.SendTextAsync(body, ct);
    }

    private WebSocketMock? GetMatchingMock(IHttpRequest request)
    {
        if (Configuration.NoMocks || !Configuration.Mocks.Any())
        {
            return null;
        }

        // The engine reports WebSocket request URLs with an http(s) scheme; normalize the
        // mock URL's ws(s) scheme so users can author either form.
        var requestUrl = ProxyUtils.NormalizeWebSocketScheme(request.Url);

        return Configuration.Mocks.FirstOrDefault(mock =>
        {
            if (string.IsNullOrEmpty(mock.Url))
            {
                return false;
            }

            var mockUrl = ProxyUtils.NormalizeWebSocketScheme(mock.Url);
            if (string.Equals(mockUrl, requestUrl, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (!mockUrl.Contains('*', StringComparison.Ordinal))
            {
                return false;
            }

            return Regex.IsMatch(requestUrl, ProxyUtils.PatternToRegex(mockUrl));
        });
    }
}
