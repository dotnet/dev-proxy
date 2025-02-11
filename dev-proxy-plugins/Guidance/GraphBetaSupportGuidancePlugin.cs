// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.DevProxy.Abstractions;
using Titanium.Web.Proxy.Http;

namespace Microsoft.DevProxy.Plugins.Guidance;

public class GraphBetaSupportGuidancePlugin(IPluginEvents pluginEvents, IProxyContext context, ILogger logger, ISet<UrlToWatch> urlsToWatch, IConfigurationSection? configSection = null) : BaseProxyPlugin(pluginEvents, context, logger, urlsToWatch, configSection)
{
    public override string Name => nameof(GraphBetaSupportGuidancePlugin);

    public override async Task RegisterAsync()
    {
        await base.RegisterAsync();

        PluginEvents.AfterResponse += AfterResponseAsync;
    }

    private Task AfterResponseAsync(object? sender, ProxyResponseArgs e)
    {
        Request request = e.Session.HttpClient.Request;
        if (UrlsToWatch is null ||
            !e.HasRequestUrlMatch(UrlsToWatch))
        {
            Logger.LogRequest("URL not matched", MessageType.Skipped, new LoggingContext(e.Session));
            return Task.CompletedTask;
        }
        if (string.Equals(e.Session.HttpClient.Request.Method, "OPTIONS", StringComparison.OrdinalIgnoreCase))
        {
            Logger.LogRequest("Skipping OPTIONS request", MessageType.Skipped, new LoggingContext(e.Session));
            return Task.CompletedTask;
        }
        if (!ProxyUtils.IsGraphBetaRequest(request))
        {
            Logger.LogRequest("Not a Microsoft Graph beta request", MessageType.Skipped, new LoggingContext(e.Session));
            return Task.CompletedTask;
        }

        Logger.LogRequest(BuildBetaSupportMessage(), MessageType.Warning, new LoggingContext(e.Session));
        return Task.CompletedTask;
    }

    private static string GetBetaSupportGuidanceUrl() => "https://aka.ms/devproxy/guidance/beta-support";
    private static string BuildBetaSupportMessage() =>
        $"Don't use beta APIs in production because they can change or be deprecated. More info at {GetBetaSupportGuidanceUrl()}";
}
