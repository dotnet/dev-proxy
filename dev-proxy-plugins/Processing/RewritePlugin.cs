// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.DevProxy.Abstractions;
using System.Text.RegularExpressions;

namespace Microsoft.DevProxy.Plugins.Processing;

public class RewriteRule
{
    public string? Url { get; set; }
}

public class RequestRewrite
{
    public RewriteRule? In { get; set; }
    public RewriteRule? Out { get; set; }
}

public class RewritePluginConfiguration
{
    public IEnumerable<RequestRewrite> Rewrites { get; set; } = [];
    public string RewritesFile { get; set; } = "rewrites.json";
}

public class RewritePlugin(IPluginEvents pluginEvents, IProxyContext context, ILogger logger, ISet<UrlToWatch> urlsToWatch, IConfigurationSection? configSection = null) : BaseProxyPlugin(pluginEvents, context, logger, urlsToWatch, configSection)
{
    public override string Name => nameof(RewritePlugin);
    private readonly RewritePluginConfiguration _configuration = new();
    private RewritesLoader? _loader = null;

    public override async Task RegisterAsync()
    {
        await base.RegisterAsync();

        ConfigSection?.Bind(_configuration);
        _loader = new RewritesLoader(Logger, _configuration);

        PluginEvents.BeforeRequest += BeforeRequestAsync;

        // make the rewrites file path relative to the configuration file
        _configuration.RewritesFile = Path.GetFullPath(
            ProxyUtils.ReplacePathTokens(_configuration.RewritesFile),
            Path.GetDirectoryName(Context.Configuration.ConfigFile ?? string.Empty) ?? string.Empty
        );

        _loader?.InitResponsesWatcher();
    }

    private Task BeforeRequestAsync(object sender, ProxyRequestArgs e)
    {
        if (UrlsToWatch is null ||
            !e.HasRequestUrlMatch(UrlsToWatch))
        {
            Logger.LogRequest("URL not matched", MessageType.Skipped, new LoggingContext(e.Session));
            return Task.CompletedTask;
        }

        if (_configuration.Rewrites is null ||
            !_configuration.Rewrites.Any())
        {
            Logger.LogRequest("No rewrites configured", MessageType.Skipped, new LoggingContext(e.Session));
            return Task.CompletedTask;
        }

        var request = e.Session.HttpClient.Request;

        foreach (var rewrite in _configuration.Rewrites)
        {
            if (string.IsNullOrEmpty(rewrite.In?.Url) ||
                string.IsNullOrEmpty(rewrite.Out?.Url))
            {
                continue;
            }

            var newUrl = Regex.Replace(request.Url, rewrite.In.Url, rewrite.Out.Url, RegexOptions.IgnoreCase);

            if (request.Url.Equals(newUrl, StringComparison.OrdinalIgnoreCase))
            {
                Logger.LogRequest($"{rewrite.In?.Url}", MessageType.Skipped, new LoggingContext(e.Session));
            }
            else
            {
                Logger.LogRequest($"{rewrite.In?.Url} > {newUrl}", MessageType.Processed, new LoggingContext(e.Session));
                request.Url = newUrl;
            }
        }

        return Task.CompletedTask;
    }
}