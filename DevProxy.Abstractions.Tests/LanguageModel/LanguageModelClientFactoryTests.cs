// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Abstractions.LanguageModel;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Net;
using System.Net.Sockets;
using Xunit;

namespace DevProxy.Abstractions.Tests.LanguageModel;

public class LanguageModelClientFactoryTests
{
    private static int GetFreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    [Fact]
    public async Task Create_DoesNotFollowRedirects_SoCredentialsStayOnOrigin()
    {
        // Start the origin listener before reserving the second port so the OS
        // can't hand out the same port twice
        var originPort = GetFreePort();
        using var origin = new HttpListener();
        origin.Prefixes.Add($"http://127.0.0.1:{originPort}/");
        origin.Start();
        var otherPort = GetFreePort();
        using var other = new HttpListener();
        other.Prefixes.Add($"http://127.0.0.1:{otherPort}/");
        other.Start();

        var otherHit = false;
        var otherTask = Task.Run(async () =>
        {
            try
            {
                var ctx = await other.GetContextAsync();
                otherHit = true;
                ctx.Response.StatusCode = 401;
                ctx.Response.Close();
            }
            catch (HttpListenerException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
        });
        var originTask = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    var ctx = await origin.GetContextAsync();
                    ctx.Response.StatusCode = 307;
                    ctx.Response.RedirectLocation = $"http://127.0.0.1:{otherPort}{ctx.Request.Url!.AbsolutePath}";
                    ctx.Response.Close();
                }
            }
            catch (HttpListenerException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
        });

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["LanguageModel:Enabled"] = "true",
                ["LanguageModel:Client"] = "AzureOpenAI",
                ["LanguageModel:Model"] = "deployment",
                ["LanguageModel:Url"] = $"http://127.0.0.1:{originPort}/openai/v1/",
                ["LanguageModel:ApiKey"] = "azure-secret"
            })
            .Build();
        using var serviceProvider = new ServiceCollection()
            .AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance)
            .AddSingleton(typeof(ILogger<>), typeof(Logger<>))
            .AddKeyedSingleton(LanguageModelClientFactory.HttpClientServiceKey, (_, _) => LanguageModelClientFactory.CreateHttpClient())
            .BuildServiceProvider();

        var client = LanguageModelClientFactory.Create(serviceProvider, configuration);

        Assert.False(await client.IsEnabledAsync(CancellationToken.None));
        Assert.False(otherHit);

        origin.Stop();
        other.Stop();
        await Task.WhenAll(originTask, otherTask);
    }
}
