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
/// Mocks WebSocket messages: matched <c>ws://</c>/<c>wss://</c> upgrades are connected
/// to the origin normally, but individual client messages that match a mock rule are
/// intercepted — the mock response is sent to the client and the message is not forwarded
/// to the origin. Unmatched messages pass through to the origin, just like HTTP mocking.
///
/// <code>
///   BeforeRequest: is this a watched WebSocket upgrade with a matching mock?
///        │ yes
///        ▼
///   session.InterceptWebSocketMessages(interceptor, onConnected)
///        │
///        ├─ onConnected: send each OnConnect message to the client
///        └─ interceptor: for each client message:
///              ├─ matches a Rule → send mock Responses, don't forward to origin
///              ├─ no match       → forward to origin (passthrough)
///              └─ CloseOnUnmatched? close the connection
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
        // Register a per-message interceptor: the engine connects to the origin and
        // relays traffic, but each client→origin message is offered to our interceptor
        // first. Matched messages get mock responses; unmatched ones pass through.
        e.ProxySession.InterceptWebSocketMessages(
            interceptor: (message, client, ct) => InterceptMessageAsync(scripted, message, client, ct),
            onConnected: scripted.OnConnect.Any()
                ? (client, ct) => SendOnConnectAsync(scripted, client, ct)
                : null);

        Logger.LogRequest($"Intercepting WebSocket {request.Url}", MessageType.Mocked, new LoggingContext(e.ProxySession));

        Logger.LogTrace("Left {Name}", nameof(BeforeRequestAsync));
        return Task.CompletedTask;
    }

    // ── per-message interceptor ────────────────────────────────────────────
    //
    //   onConnected: send OnConnect messages
    //   interceptor: for each client message:
    //     └─ first Rule whose Match matches → send Responses (+ optional close) → return true
    //        no match → CloseOnUnmatched ? close + return true : return false (passthrough)

    private static async Task SendOnConnectAsync(
        WebSocketMock mock, IWebSocketConnection client, CancellationToken ct)
    {
        foreach (var message in mock.OnConnect)
        {
            await SendAsync(client, message, ct).ConfigureAwait(false);
        }
    }

    private static async Task<bool> InterceptMessageAsync(
        WebSocketMock mock, WebSocketMessage message, IWebSocketConnection client, CancellationToken ct)
    {
        if (message.Type == FrameworkWsMessageType.Close)
        {
            return false; // let the relay handle close propagation
        }

        var text = message.Text;
        var rule = mock.Rules.FirstOrDefault(r => WebSocketMessageMatcher.Matches(r.Match, text));
        if (rule is null)
        {
            if (mock.CloseOnUnmatched)
            {
                await client.CloseAsync(ct).ConfigureAwait(false);
                return true;
            }
            return false; // no match — forward to origin
        }

        foreach (var response in rule.Responses)
        {
            await SendAsync(client, response, ct).ConfigureAwait(false);
        }

        if (rule.CloseAfter)
        {
            await client.CloseAsync(ct).ConfigureAwait(false);
        }

        return true; // handled — don't forward to origin
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
