#:property PublishAot=false
// .NET smoke client: HttpClient and ClientWebSocket through a WebProxy, trusting
// only the Dev Proxy root certificate (no OS trust store changes).
// Run: dotnet run smoke.cs
// Env: SMOKE_PROXY_URL, SMOKE_TARGET_URL, SMOKE_CA_FILE, SMOKE_SSE_MIN_SPAN_MS
// Prints one "RESULT<TAB>scenario<TAB>PASS|FAIL|SKIP<TAB>detail" line per scenario.

using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;

var proxyUrl = Env("SMOKE_PROXY_URL");
var target = Env("SMOKE_TARGET_URL");
var sseMinSpanMs = int.Parse(Env("SMOKE_SSE_MIN_SPAN_MS"), System.Globalization.CultureInfo.InvariantCulture);
var root = X509CertificateLoader.LoadCertificateFromFile(Env("SMOKE_CA_FILE"));

// WebProxy bypasses nothing by default (BypassProxyOnLocal = false), so localhost goes through the proxy.
var proxy = new WebProxy(proxyUrl);
RemoteCertificateValidationCallback validate = (_, cert, _, errors) =>
{
    if (cert is null || (errors & ~SslPolicyErrors.RemoteCertificateChainErrors) != SslPolicyErrors.None)
    {
        return false;
    }
    using var chain = new X509Chain();
    chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
    chain.ChainPolicy.CustomTrustStore.Add(root);
    chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
    return chain.Build(new X509Certificate2(cert));
};

using var http = new HttpClient(new SocketsHttpHandler
{
    Proxy = proxy,
    UseProxy = true,
    SslOptions = { RemoteCertificateValidationCallback = validate },
})
{
    Timeout = TimeSpan.FromSeconds(30),
};

await Run("mock", async () =>
{
    using var res = await http.GetAsync($"{target}/mocked");
    var body = await res.Content.ReadAsStringAsync();
    var mocked = JsonDocument.Parse(body).RootElement.TryGetProperty("mocked", out var m) && m.ValueKind == JsonValueKind.True;
    return (int)res.StatusCode == 200 && Header(res, "X-Smoke-Mock") == "yes" && mocked
        ? null
        : $"status={(int)res.StatusCode} x-smoke-mock={Header(res, "X-Smoke-Mock")} body={body}";
});

await Run("passthrough", async () =>
{
    using var req = new HttpRequestMessage(HttpMethod.Get, $"{target}/passthrough");
    req.Headers.Add("X-Smoke-Request", "hello-dotnet");
    using var res = await http.SendAsync(req);
    var body = await res.Content.ReadAsStringAsync();
    var echoed = JsonDocument.Parse(body).RootElement.GetProperty("smokeRequest").GetString();
    return (int)res.StatusCode == 203 && Header(res, "X-Smoke-Echo") == "hello-dotnet" && echoed == "hello-dotnet"
        ? null
        : $"status={(int)res.StatusCode} x-smoke-echo={Header(res, "X-Smoke-Echo")} body={body}";
});

await Run("upload", async () =>
{
    var data = RandomNumberGenerator.GetBytes(5 * 1024 * 1024);
    var expectedSha = Convert.ToHexStringLower(SHA256.HashData(data));
    using var req = new HttpRequestMessage(HttpMethod.Post, $"{target}/upload")
    {
        Content = new StreamContent(new MemoryStream(data), 64 * 1024),
    };
    req.Headers.TransferEncodingChunked = true;
    req.Content.Headers.ContentType = new("application/octet-stream");
    using var res = await http.SendAsync(req);
    var body = await res.Content.ReadAsStringAsync();
    var json = JsonDocument.Parse(body).RootElement;
    return (int)res.StatusCode == 200 && json.GetProperty("bytes").GetInt64() == data.Length && json.GetProperty("sha256").GetString() == expectedSha
        ? null
        : $"status={(int)res.StatusCode} body={body} expected bytes={data.Length} sha256={expectedSha}";
});

await Run("sse", async () =>
{
    using var res = await http.GetAsync($"{target}/sse", HttpCompletionOption.ResponseHeadersRead);
    using var reader = new StreamReader(await res.Content.ReadAsStreamAsync());
    var events = new List<(string Data, long At)>();
    var clock = Stopwatch.StartNew();
    while (await reader.ReadLineAsync() is { } line)
    {
        if (line.StartsWith("data:", StringComparison.Ordinal))
        {
            events.Add((line[5..].Trim(), clock.ElapsedMilliseconds));
        }
    }
    var ids = string.Join(',', events.Select(e => e.Data));
    var span = events.Count > 0 ? events[^1].At - events[0].At : 0;
    return (int)res.StatusCode == 200 && ids == "1,2,3" && span >= sseMinSpanMs
        ? null
        : $"status={(int)res.StatusCode} events=[{ids}] span={span}ms (min {sseMinSpanMs}ms)";
});

await Run("websocket", async () =>
{
    using var ws = new ClientWebSocket();
    ws.Options.Proxy = proxy;
    ws.Options.RemoteCertificateValidationCallback = validate;
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
    await ws.ConnectAsync(new Uri($"{target.Replace("https://", "wss://")}/ws"), cts.Token);
    (WebSocketMessageType Type, byte[] Data)[] messages =
    [
        (WebSocketMessageType.Text, Encoding.UTF8.GetBytes("hello-dotnet")),
        (WebSocketMessageType.Text, Encoding.UTF8.GetBytes(new string('x', 70_000))),
        (WebSocketMessageType.Binary, [.. Enumerable.Range(0, 256).Select(i => (byte)i)]),
    ];
    var received = new List<(WebSocketMessageType Type, byte[] Data)>();
    foreach (var message in messages)
    {
        await ws.SendAsync(message.Data, message.Type, true, cts.Token);
        received.Add(await ReceiveMessage(ws, cts.Token));
    }
    // CloseAsync throws unless the server's Close reply comes back through the relay.
    await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, null, cts.Token);
    if (received.Count != messages.Length
        || !received.Zip(messages).All(p => p.First.Type == p.Second.Type && p.First.Data.AsSpan().SequenceEqual(p.Second.Data)))
    {
        return $"echo mismatch: {string.Join(',', received.Select(r => $"{r.Type}:{r.Data.Length}"))}";
    }
    return ws.State == WebSocketState.Closed && ws.CloseStatus == WebSocketCloseStatus.NormalClosure
        ? null
        : $"unclean close: state={ws.State} status={ws.CloseStatus}";
});

static async Task<(WebSocketMessageType Type, byte[] Data)> ReceiveMessage(ClientWebSocket ws, CancellationToken ct)
{
    using var buffer = new MemoryStream();
    var chunk = new byte[16 * 1024];
    WebSocketReceiveResult received;
    do
    {
        received = await ws.ReceiveAsync(chunk, ct);
        buffer.Write(chunk, 0, received.Count);
    } while (!received.EndOfMessage);
    return (received.MessageType, buffer.ToArray());
}

static async Task Run(string name, Func<Task<string?>> scenario)
{
    string? failure;
    try
    {
        failure = await scenario();
    }
    catch (Exception ex)
    {
        failure = $"{ex.GetType().Name}: {ex.Message}{(ex.InnerException is null ? "" : $" ({ex.InnerException.Message})")}";
    }
    var detail = string.Join(' ', (failure ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    Console.WriteLine($"RESULT\t{name}\t{(failure is null ? "PASS" : "FAIL")}\t{detail}");
}

static string? Header(HttpResponseMessage res, string name) =>
    res.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;

static string Env(string name) =>
    Environment.GetEnvironmentVariable(name) ?? throw new InvalidOperationException($"{name} is not set");
