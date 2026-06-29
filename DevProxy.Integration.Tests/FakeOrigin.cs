// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Globalization;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DevProxy.Integration.Tests;

/// <summary>
/// A deterministic upstream origin server used as the integration target. Each scenario
/// asserts the proxy faithfully relays this origin's responses.
///
/// <code>
///   GET  /get            → 200 "hello get"          (plain finite body)
///   POST /echo           → 200 echoes request body  (X-Echo-Length header)
///   GET  /status/{code}  → {code} "status {code}"
///   GET  /headers        → 200, reflects X-Probe request header in body
///   GET  /sse            → 200 text/event-stream, 5 events flushed ~50ms apart
///   GET  /big/{n}        → 200, n bytes of 'A' (large finite body)
///   GET  /json           → 200 application/json, a 2-element array
/// </code>
/// </summary>
internal sealed class FakeOrigin : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly System.Collections.Concurrent.ConcurrentQueue<ReceivedRequest> _received;

    public int Port { get; }

    public string Host => $"127.0.0.1:{Port.ToString(CultureInfo.InvariantCulture)}";

    /// <summary>Every request this origin actually received, in arrival order.</summary>
    public IReadOnlyCollection<ReceivedRequest> ReceivedRequests => _received.ToArray();

    private FakeOrigin(WebApplication app, int port, System.Collections.Concurrent.ConcurrentQueue<ReceivedRequest> received)
    {
        _app = app;
        Port = port;
        _received = received;
    }

    public static async Task<FakeOrigin> StartAsync()
    {
        var port = NetUtil.GetFreePort();
        var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions());
        _ = builder.WebHost.UseKestrelCore();
        _ = builder.Logging.ClearProviders();
        _ = builder.Services.AddRouting();
        builder.WebHost.ConfigureKestrel(options =>
            options.Listen(System.Net.IPAddress.Loopback, port, listen =>
                listen.Protocols = HttpProtocols.Http1));

        var app = builder.Build();

        var received = new System.Collections.Concurrent.ConcurrentQueue<ReceivedRequest>();
        app.Use(async (ctx, next) =>
        {
            received.Enqueue(new ReceivedRequest(ctx.Request.Method, ctx.Request.Path + ctx.Request.QueryString));
            await next().ConfigureAwait(false);
        });

        app.MapGet("/get", () => Results.Text("hello get"));

        app.MapPost("/echo", async (HttpContext ctx) =>
        {
            using var reader = new StreamReader(ctx.Request.Body);
            var body = await reader.ReadToEndAsync().ConfigureAwait(false);
            ctx.Response.Headers["X-Echo-Length"] =
                body.Length.ToString(CultureInfo.InvariantCulture);
            await ctx.Response.WriteAsync(body).ConfigureAwait(false);
        });

        app.MapGet("/status/{code:int}", (int code) =>
            Results.Text(
                $"status {code.ToString(CultureInfo.InvariantCulture)}",
                statusCode: code));

        app.MapGet("/nocontent", () => Results.StatusCode(204));

        app.MapGet("/headers", (HttpContext ctx) =>
        {
            var probe = ctx.Request.Headers["X-Probe"].ToString();
            return Results.Text($"probe={probe}");
        });

        app.MapGet("/sse", async (HttpContext ctx) =>
        {
            ctx.Response.Headers.ContentType = "text/event-stream";
            for (var i = 0; i < 5; i++)
            {
                var payload = Encoding.UTF8.GetBytes(
                    $"data: event-{i.ToString(CultureInfo.InvariantCulture)}\n\n");
                await ctx.Response.Body.WriteAsync(payload).ConfigureAwait(false);
                await ctx.Response.Body.FlushAsync().ConfigureAwait(false);
                await Task.Delay(50).ConfigureAwait(false);
            }
        });

        app.MapGet("/big/{n:int}", (int n) =>
            Results.Text(new string('A', n)));

        app.MapGet("/json", () => Results.Json(new[]
        {
            new { id = 1, name = "alpha" },
            new { id = 2, name = "beta" },
        }));

        await app.StartAsync().ConfigureAwait(false);
        return new FakeOrigin(app, port, received);
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync().ConfigureAwait(false);
        await _app.DisposeAsync().ConfigureAwait(false);
    }
}

internal sealed record ReceivedRequest(string Method, string PathAndQuery);
