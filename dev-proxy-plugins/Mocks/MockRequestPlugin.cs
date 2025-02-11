// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.DevProxy.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Microsoft.DevProxy.Plugins.Mocks;

public class MockRequestConfiguration
{
    [JsonIgnore]
    public string MockFile { get; set; } = "mock-request.json";
    public MockRequest? Request { get; set; }
}

public class MockRequestPlugin(IPluginEvents pluginEvents, IProxyContext context, ILogger logger, ISet<UrlToWatch> urlsToWatch, IConfigurationSection? configSection = null) : BaseProxyPlugin(pluginEvents, context, logger, urlsToWatch, configSection)
{
    protected MockRequestConfiguration _configuration = new();
    private MockRequestLoader? _loader = null;

    public override string Name => nameof(MockRequestPlugin);

    public override async Task RegisterAsync()
    {
        await base.RegisterAsync();

        ConfigSection?.Bind(_configuration);
        _loader = new MockRequestLoader(Logger, _configuration);

        PluginEvents.MockRequest += OnMockRequestAsync;

        // make the mock file path relative to the configuration file
        _configuration.MockFile = Path.GetFullPath(ProxyUtils.ReplacePathTokens(_configuration.MockFile), Path.GetDirectoryName(Context.Configuration.ConfigFile ?? string.Empty) ?? string.Empty);

        // load the request from the configured mock file
        _loader.InitResponsesWatcher();
    }

    protected HttpRequestMessage GetRequestMessage()
    {
        Debug.Assert(_configuration.Request is not null, "The mock request is not configured");

        Logger.LogDebug("Preparing mock {method} request to {url}", _configuration.Request.Method, _configuration.Request.Url);
        var requestMessage = new HttpRequestMessage
        {
            RequestUri = new Uri(_configuration.Request.Url),
            Method = new HttpMethod(_configuration.Request.Method)
        };

        var contentType = "";
        if (_configuration.Request.Headers is not null)
        {
            Logger.LogDebug("Adding headers to the mock request");

            foreach (var header in _configuration.Request.Headers)
            {
                if (header.Name.Equals("content-type", StringComparison.CurrentCultureIgnoreCase))
                {
                    contentType = header.Value;
                    continue;
                }

                requestMessage.Headers.Add(header.Name, header.Value);
            }
        }

        if (_configuration.Request.Body is not null)
        {
            Logger.LogDebug("Adding body to the mock request");

            if (_configuration.Request.Body is string)
            {
                requestMessage.Content = new StringContent(_configuration.Request.Body, Encoding.UTF8, contentType);
            }
            else
            {
                requestMessage.Content = new StringContent(JsonSerializer.Serialize(_configuration.Request.Body, ProxyUtils.JsonSerializerOptions), Encoding.UTF8, "application/json");
            }
        }

        return requestMessage;
    }

    protected virtual async Task OnMockRequestAsync(object sender, EventArgs e)
    {
        if (_configuration.Request is null)
        {
            Logger.LogDebug("No mock request is configured. Skipping.");
            return;
        }

        using var httpClient = new HttpClient();
        var requestMessage = GetRequestMessage();

        try
        {
            Logger.LogRequest("Sending mock request", MessageType.Mocked, _configuration.Request.Method, _configuration.Request.Url);

            await httpClient.SendAsync(requestMessage);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "An error has occurred while sending the mock request to {url}", _configuration.Request.Url);
        }
    }
}