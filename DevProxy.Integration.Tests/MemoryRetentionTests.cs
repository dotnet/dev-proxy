// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using DevProxy.Abstractions.Plugins;
using DevProxy.Abstractions.Proxy;
using DevProxy.Commands;
using DevProxy.Logging;
using DevProxy.Plugins.Mocking;
using DevProxy.Proxy;
using DevProxy.Proxy.Kestrel;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Options;
using Xunit;
using Xunit.Abstractions;

namespace DevProxy.Integration.Tests;

/// <summary>
/// Checks that Dev Proxy doesn't keep memory alive across proxied requests and hot
/// reloads. The checks use <see cref="WeakReference"/> where the retained objects are
/// reachable, so they're exact, and otherwise compare the managed heap after a forced
/// full collection, which is stable on CI runners where timing isn't.
///
/// Each check has a canary test that adds a deliberate leak (a plugin that appends
/// every request or generation to a static list) and asserts the check catches it, so
/// the checks can't silently pass because they measure nothing.
/// </summary>
public sealed class MemoryRetentionTests(ITestOutputHelper output)
{
    private const string HotReloadSkipReason =
        "Every previous hot-reload generation stays reachable on main. Passes with https://github.com/dotnet/dev-proxy/pull/2153.";

    private const string FailedRequestsSkipReason =
        "Request logs of failed and cancelled requests stay buffered on main. Passes with https://github.com/dotnet/dev-proxy/pull/2151.";

    private const int HotReloadGenerations = 20;

    // Allows for pools that keep growing towards their peak use long after warm-up.
    // Heap snapshots show 10,000-30,000 requests adding 2-4 MB, nearly all of it 4 KB
    // blocks in Kestrel's memory pool. A leak of one small object per request would
    // stay under it, which is why the tests also track sessions with weak references.
    private const long AllowedGrowthBytes = 8 * 1024 * 1024;

    private static readonly HttpClient SharedHttpClient = new();
    private static readonly TestProxyConfiguration ProxyConfig = new();

    private readonly ITestOutputHelper _output = output;

    [Fact(Skip = HotReloadSkipReason)]
    public async Task HotReload_ReleasesPreviousGenerations()
    {
        var result = await MeasureHotReloadAsync(HotReloadGenerations, leakGenerations: false);

        Assert.True(
            result.RetainedGenerations == 0,
            $"{result.RetainedGenerations} of {result.Generations} hot-reload generations are still reachable after a full collection ({result.RetainedObjects}).");
    }

    [Fact]
    public async Task HotReload_DetectsGenerationsHeldByALeak()
    {
        var result = await MeasureHotReloadAsync(generations: 3, leakGenerations: true);

        Assert.Equal(result.Generations, result.RetainedGenerations);
    }

    [Fact]
    public async Task Requests_DoNotRetainMemory()
    {
        var result = await MeasureRequestsAsync(
            RequestMix.Successful, warmupRequests: 1_000, measuredRequests: 10_000, leakSessions: false);

        Assert.True(
            result.RetainedSessions == 0,
            $"{result.RetainedSessions} of {result.TrackedSessions} proxy sessions are still reachable after their requests completed.");
        Assert.True(
            result.GrowthBytes < AllowedGrowthBytes,
            $"The managed heap grew by {result.GrowthBytes:N0} bytes over {result.MeasuredRequests:N0} requests; allowed: {AllowedGrowthBytes:N0}.");
    }

    [Fact(Skip = FailedRequestsSkipReason)]
    public async Task FailedAndCancelledRequests_DoNotRetainMemory()
    {
        var result = await MeasureRequestsAsync(
            RequestMix.FailedAndCancelled, warmupRequests: 200, measuredRequests: 1_000, leakSessions: false);

        Assert.True(
            result.RetainedSessions == 0,
            $"{result.RetainedSessions} of {result.TrackedSessions} proxy sessions are still reachable after their requests failed or were cancelled.");
        Assert.True(
            result.GrowthBytes < AllowedGrowthBytes,
            $"The managed heap grew by {result.GrowthBytes:N0} bytes over {result.MeasuredRequests:N0} requests; allowed: {AllowedGrowthBytes:N0}.");
    }

    [Fact]
    public async Task Requests_DetectSessionsHeldByALeak()
    {
        var result = await MeasureRequestsAsync(
            RequestMix.Successful, warmupRequests: 100, measuredRequests: 1_000, leakSessions: true);

        Assert.Equal(result.TrackedSessions, result.RetainedSessions);
        Assert.True(
            result.GrowthBytes >= AllowedGrowthBytes,
            $"A plugin that keeps every session grew the managed heap by only {result.GrowthBytes:N0} bytes; the check needs at least {AllowedGrowthBytes:N0} to catch it.");
    }

    #region Hot reload

    private sealed record HotReloadResult(int Generations, int RetainedGenerations, string RetainedObjects);

    private async Task<HotReloadResult> MeasureHotReloadAsync(int generations, bool leakGenerations)
    {
        await using var origin = await FakeOrigin.StartAsync(recordRequests: false);
        var configDir = Directory.CreateTempSubdirectory("devproxy-memory-");
        var configPath = Path.Combine(configDir.FullName, "devproxyrc.json");
        await File.WriteAllTextAsync(Path.Combine(configDir.FullName, "mocks.json"), MocksFile(origin.Host));
        await File.WriteAllTextAsync(configPath, ConfigFile(origin.Host, generation: 0));

        string[] args = [DevProxyCommand.ConfigFileOptionName, configPath];
        var options = new DevProxyConfigOptions();
        options.ParseOptions(args);
        var proxyEnvironment = new HttpProxyEnvironment();

        var generationRefs = new List<GenerationRefs>();
        try
        {
            for (var generation = 1; generation <= generations; generation++)
            {
                generationRefs.Add(await RunGenerationAsync(
                    args, options, proxyEnvironment, configPath, origin.Host, generation, leakGenerations));
            }

            await CollectUntilAsync(() => generationRefs.All(g => !g.IsAlive));
            var retained = generationRefs.Where(g => g.IsAlive).ToArray();
            var retainedObjects = string.Join(
                ", ",
                retained.SelectMany(g => g.AliveObjects()).GroupBy(n => n).Select(g => $"{g.Key} x{g.Count()}"));

            _output.WriteLine($"Hot-reload generations checked: {generations}");
            _output.WriteLine($"Hot-reload generations retained: {retained.Length}");
            if (retained.Length > 0)
            {
                _output.WriteLine($"Retained objects: {retainedObjects}");
            }

            return new(generations, retained.Length, retainedObjects);
        }
        finally
        {
            LeakedGenerations.Clear();
            ConfigFileWatcher.Reset();
            try
            {
                configDir.Delete(recursive: true);
            }
            catch (IOException)
            {
                // A late file watcher can still hold the folder; it's a temp folder.
            }
        }
    }

    /// <summary>The deliberate leak for the hot-reload canary.</summary>
    private static readonly List<object> LeakedGenerations = [];

    private sealed class GenerationRefs(Dictionary<string, WeakReference> objects)
    {
        public bool IsAlive => objects.Values.Any(r => r.IsAlive);

        public IEnumerable<string> AliveObjects() => objects.Where(o => o.Value.IsAlive).Select(o => o.Key);
    }

    /// <summary>
    /// Runs one generation the way Program.cs does on hot reload: builds the host from
    /// the config file, starts it, proxies a few requests, then rewrites the config file
    /// and waits for the config watcher to stop the host. Returns only weak references so
    /// nothing in this frame keeps the generation alive.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<GenerationRefs> RunGenerationAsync(
        string[] args,
        DevProxyConfigOptions options,
        HttpProxyEnvironment proxyEnvironment,
        string configPath,
        string originHost,
        int generation,
        bool leakGeneration)
    {
        ConfigFileWatcher.Reset();
        using var environment = new ProcessEnvironment();
        var app = ProxyHost.BuildApplication(options, environment, proxyEnvironment);
        try
        {
            // Mirror the start-up steps of DevProxyCommand.InvokeAsync that set up plugins,
            // without the ones that write state for other Dev Proxy commands to find.
            var command = app.Services.GetRequiredService<DevProxyCommand>();
            var plugins = app.Services.GetServices<IPlugin>().ToArray();
            Assert.NotEmpty(plugins);
            var optionsLoadedArgs = new OptionsLoadedArgs(command.Parse(args));
            foreach (var plugin in plugins.Where(p => p.Enabled))
            {
                plugin.OptionsLoaded(optionsLoadedArgs);
            }
            if (leakGeneration)
            {
                LeakedGenerations.Add(plugins);
            }

            var stopping = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var stoppingRegistration = app.Lifetime.ApplicationStopping.Register(() => stopping.TrySetResult());

            await app.StartAsync();
            var engines = app.Services.GetServices<IHostedService>().OfType<KestrelProxyEngine>().ToArray();
            Assert.All(await Task.WhenAll(engines.Select(e => e.Started)), Assert.True);

            var proxyPort = app.Services.GetRequiredService<IProxyConfiguration>().Port;
            await SendGenerationRequestsAsync(proxyPort, originHost);

            // The watcher debounces changes and may start after the first write, so keep
            // rewriting until it restarts the host.
            for (var attempt = 0; !stopping.Task.IsCompleted; attempt++)
            {
                Assert.True(attempt < 10, $"Generation {generation} didn't restart after the config file changed.");
                await File.WriteAllTextAsync(configPath, ConfigFile(originHost, generation * 100 + attempt));
                _ = await Task.WhenAny(stopping.Task, Task.Delay(TimeSpan.FromSeconds(3)));
            }
            Assert.True(ConfigFileWatcher.IsRestarting);

            var objects = new Dictionary<string, WeakReference>
            {
                ["WebApplication"] = new(app),
                ["IServiceProvider"] = new(app.Services),
                ["DevProxyCommand"] = new(command),
            };
            foreach (var plugin in plugins)
            {
                objects[plugin.Name] = new(plugin);
            }
            return new(objects);
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    private static async Task SendGenerationRequestsAsync(int proxyPort, string originHost)
    {
        using var handler = new HttpClientHandler
        {
            Proxy = new WebProxy($"http://127.0.0.1:{proxyPort.ToString(CultureInfo.InvariantCulture)}"),
            UseProxy = true,
        };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };

        using (var mocked = await client.GetAsync(new Uri($"http://{originHost}/mocked")))
        {
            Assert.Equal(HttpStatusCode.Created, mocked.StatusCode);
        }
        using (var passthrough = await client.GetAsync(new Uri($"http://{originHost}/get")))
        {
            Assert.Equal(HttpStatusCode.OK, passthrough.StatusCode);
        }
    }

    private static string ConfigFile(string originHost, int generation) => $$"""
        {
          "plugins": [
            {
              "name": "MockResponsePlugin",
              "enabled": true,
              "pluginPath": "{{JsonEncodedPluginPath()}}",
              "configSection": "mocksPlugin"
            }
          ],
          "urlsToWatch": [
            "http://{{originHost}}/*"
          ],
          "mocksPlugin": {
            "mocksFile": "mocks.json"
          },
          "port": 0,
          "apiPort": 0,
          "asSystemProxy": false,
          "installCert": false,
          "logLevel": "error",
          "newVersionNotification": "none",
          "generation": {{generation.ToString(CultureInfo.InvariantCulture)}}
        }
        """;

    private static string MocksFile(string originHost) => $$"""
        {
          "mocks": [
            {
              "request": { "url": "http://{{originHost}}/mocked", "method": "GET" },
              "response": { "statusCode": 201, "body": "mocked" }
            }
          ]
        }
        """;

    private static string JsonEncodedPluginPath() =>
        System.Text.Json.JsonEncodedText.Encode(LocatePluginsDll()).ToString();

    /// <summary>
    /// Walks up from the test assembly output to the repo root and resolves the plugins
    /// the host's build copies next to it, for the same configuration (Debug/Release).
    /// </summary>
    private static string LocatePluginsDll()
    {
        var config =
#if DEBUG
            "Debug";
#else
            "Release";
#endif
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "DevProxy", "bin", config, "net10.0", "plugins", "DevProxy.Plugins.dll");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException("Couldn't find DevProxy.Plugins.dll in the DevProxy build output. Build DevProxy.sln first.");
    }

    #endregion

    #region Requests

    private enum RequestMix
    {
        /// <summary>Mocked and passed-through requests, with large request and response bodies.</summary>
        Successful,

        /// <summary>Requests the origin fails and requests the client cancels.</summary>
        FailedAndCancelled,
    }

    private sealed record RequestsResult(
        int MeasuredRequests, long GrowthBytes, int TrackedSessions, int RetainedSessions);

    private const int Concurrency = 8;
    private const int TrackedRequests = 200;
    private const int RequestBodyBytes = 64 * 1024;
    private const int ResponseBodyBytes = 256 * 1024;

    private async Task<RequestsResult> MeasureRequestsAsync(
        RequestMix mix, int warmupRequests, int measuredRequests, bool leakSessions)
    {
        await using var origin = await FakeOrigin.StartAsync(recordRequests: false);
        var urls = KestrelProxyHarness.BuildUrlsToWatch(origin.Host);

        var probe = new SessionProbePlugin(urls, leakSessions);
        var mockPlugin = new MockResponsePlugin(
            SharedHttpClient,
            NullLogger<MockResponsePlugin>.Instance,
            urls,
            ProxyConfig,
            PluginConfig.FromJson($$"""
                {
                  "mocks": [
                    {
                      "request": { "url": "http://{{origin.Host}}/mocked", "method": "GET" },
                      "response": { "statusCode": 201, "body": "mocked" }
                    }
                  ]
                }
                """));

        // Log through the real console formatter, which buffers each request's log lines
        // until the request finishes, into a writer that discards the output.
        using var loggerProvider = new FormatterLoggerProvider(new ProxyConsoleFormatter(
            Options.Create(new ProxyConsoleFormatterOptions { IncludeScopes = true })));
        using var loggerFactory = LoggerFactory.Create(builder => builder
            .SetMinimumLevel(LogLevel.Trace)
            .AddProvider(loggerProvider));

        try
        {
            await using var proxy = await KestrelProxyHarness.StartAsync(
                origin.Host, [probe, mockPlugin], loggerFactory);

            await SendRequestsAsync(proxy, origin.Host, mix, warmupRequests);
            var before = await MeasureHeapAsync();

            await SendRequestsAsync(proxy, origin.Host, mix, measuredRequests);
            var after = await MeasureHeapAsync();

            probe.Tracking = true;
            await SendRequestsAsync(proxy, origin.Host, mix, TrackedRequests);
            probe.Tracking = false;

            var tracked = probe.TrackedSessions.ToArray();
            await CollectUntilAsync(() => tracked.All(s => !s.IsAlive));
            var retained = tracked.Count(s => s.IsAlive);

            var growth = after - before;
            _output.WriteLine($"Request mix: {mix}");
            _output.WriteLine($"Warm-up requests: {warmupRequests:N0}");
            _output.WriteLine($"Measured requests: {measuredRequests:N0}");
            _output.WriteLine($"Managed heap after warm-up: {before:N0} bytes");
            _output.WriteLine($"Managed heap after measured requests: {after:N0} bytes");
            _output.WriteLine($"Bytes retained: {growth:N0} (allowed: {AllowedGrowthBytes:N0})");
            _output.WriteLine($"Sessions tracked: {tracked.Length}, retained: {retained}");

            return new(measuredRequests, growth, tracked.Length, retained);
        }
        finally
        {
            SessionProbePlugin.LeakedSessions.Clear();
        }
    }

    private static async Task SendRequestsAsync(KestrelProxyHarness proxy, string originHost, RequestMix mix, int count)
    {
        // A client per phase, so that closing its connections releases anything the proxy
        // keeps per connection before the heap is measured.
        using var client = proxy.CreateHttpClient();
        var next = -1;
        var workers = Enumerable.Range(0, Concurrency).Select(async _ =>
        {
            int i;
            while ((i = Interlocked.Increment(ref next)) < count)
            {
                await SendRequestAsync(client, originHost, mix, i);
            }
        });
        await Task.WhenAll(workers);
    }

    private static readonly byte[] RequestBody = Encoding.ASCII.GetBytes(new string('B', RequestBodyBytes));

    private static async Task SendRequestAsync(HttpClient client, string originHost, RequestMix mix, int i)
    {
        if (mix == RequestMix.Successful)
        {
            switch (i % 4)
            {
                case 0:
                    {
                        using var response = await client.GetAsync(new Uri($"http://{originHost}/mocked"));
                        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
                        _ = await response.Content.ReadAsByteArrayAsync();
                        break;
                    }
                case 1:
                    {
                        using var response = await client.GetAsync(new Uri($"http://{originHost}/get"));
                        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                        _ = await response.Content.ReadAsByteArrayAsync();
                        break;
                    }
                case 2:
                    {
                        using var content = new ByteArrayContent(RequestBody);
                        using var response = await client.PostAsync(new Uri($"http://{originHost}/echo"), content);
                        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                        Assert.Equal(RequestBodyBytes, (await response.Content.ReadAsByteArrayAsync()).Length);
                        break;
                    }
                default:
                    {
                        using var response = await client.GetAsync(new Uri($"http://{originHost}/big/{ResponseBodyBytes}"));
                        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                        Assert.Equal(ResponseBodyBytes, (await response.Content.ReadAsByteArrayAsync()).Length);
                        break;
                    }
            }
            return;
        }

        if (i % 2 == 0)
        {
            // The origin drops the connection, so the proxy answers with a gateway error.
            using var content = new ByteArrayContent(RequestBody);
            using var response = await client.PostAsync(new Uri($"http://{originHost}/abort"), content);
            Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        }
        else
        {
            // The client gives up while the origin is still working on the response.
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
            using var content = new ByteArrayContent(RequestBody);
            _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => client.PostAsync(new Uri($"http://{originHost}/delay/2000"), content, cts.Token));
        }
    }

    private static async Task<long> MeasureHeapAsync()
    {
        // Let the proxy finish tearing down connections the client just closed.
        await Task.Delay(250);
        _ = GC.GetTotalMemory(forceFullCollection: true);
        return GC.GetTotalMemory(forceFullCollection: true);
    }

    /// <summary>
    /// Tracks proxy sessions with weak references while <see cref="Tracking"/> is on.
    /// When created to leak, it also keeps every session in a static list: the
    /// deliberate leak the canary tests use.
    /// </summary>
    private sealed class SessionProbePlugin(ISet<UrlToWatch> urlsToWatch, bool leak)
        : BasePlugin(NullLogger.Instance, urlsToWatch)
    {
        public static readonly ConcurrentQueue<object> LeakedSessions = new();

        public override string Name => nameof(SessionProbePlugin);

        public volatile bool Tracking;

        public ConcurrentQueue<WeakReference> TrackedSessions { get; } = new();

        public override Task BeforeRequestAsync(ProxyRequestArgs e, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(e);
            if (leak)
            {
                LeakedSessions.Enqueue(e.ProxySession);
            }
            if (Tracking)
            {
                TrackedSessions.Enqueue(new(e.ProxySession));
            }
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Writes log entries through a console formatter like the console logger does, but
    /// synchronously and into <see cref="TextWriter.Null"/>.
    /// </summary>
    private sealed class FormatterLoggerProvider(ConsoleFormatter formatter) : ILoggerProvider, ISupportExternalScope
    {
        private IExternalScopeProvider? _scopeProvider;

        public ILogger CreateLogger(string categoryName) => new FormatterLogger(this, categoryName);

        public void SetScopeProvider(IExternalScopeProvider scopeProvider) => _scopeProvider = scopeProvider;

        public void Dispose()
        {
        }

        private sealed class FormatterLogger(FormatterLoggerProvider provider, string category) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull =>
                provider._scopeProvider?.Push(state);

            public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                var entry = new Microsoft.Extensions.Logging.Abstractions.LogEntry<TState>(
                    logLevel, category, eventId, state, exception, formatter);
                provider.Write(entry);
            }
        }

        private void Write<TState>(in Microsoft.Extensions.Logging.Abstractions.LogEntry<TState> entry)
        {
            lock (formatter)
            {
                formatter.Write(entry, _scopeProvider, TextWriter.Null);
            }
        }
    }

    #endregion

    /// <summary>
    /// Forces full collections until <paramref name="released"/> holds, giving background
    /// work that still references the objects (connection teardown, file watchers) a
    /// moment to finish. Returns without failing; callers assert what's left.
    /// </summary>
    private static async Task CollectUntilAsync(Func<bool> released)
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            if (released())
            {
                return;
            }
            await Task.Delay(100);
        }
    }
}
