// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Abstractions.Proxy;
using DevProxy.Abstractions.Plugins;
using DevProxy.Abstractions.Utils;
using Microsoft.Extensions.Logging;
using Titanium.Web.Proxy.EventArguments;
using System.Globalization;

namespace DevProxy.Plugins.Guidance;

public sealed class GraphSelectGuidancePlugin(
    ILogger<GraphSelectGuidancePlugin> logger,
    ISet<UrlToWatch> urlsToWatch) : BasePlugin(logger, urlsToWatch)
{
    public override string Name => nameof(GraphSelectGuidancePlugin);

    public override async Task InitializeAsync(InitArgs e, CancellationToken cancellationToken)
    {
        await base.InitializeAsync(e, cancellationToken);

        // let's not await so that it doesn't block the proxy startup
        _ = MSGraphDbUtils.GenerateMSGraphDbAsync(Logger, true, cancellationToken);
    }

    public override Task AfterResponseAsync(ProxyResponseArgs e, CancellationToken cancellationToken)
    {
        Logger.LogTrace("{Method} called", nameof(AfterResponseAsync));

        ArgumentNullException.ThrowIfNull(e);

        if (!e.HasRequestUrlMatch(UrlsToWatch))
        {
            Logger.LogRequest("URL not matched", MessageType.Skipped, new(e.Session));
            return Task.CompletedTask;
        }
        if (string.Equals(e.Session.HttpClient.Request.Method, "OPTIONS", StringComparison.OrdinalIgnoreCase))
        {
            Logger.LogRequest("Skipping OPTIONS request", MessageType.Skipped, new(e.Session));
            return Task.CompletedTask;
        }

        if (WarnNoSelect(e.Session))
        {
            Logger.LogRequest(BuildUseSelectMessage(), MessageType.Warning, new(e.Session));
        }

        Logger.LogTrace("Left {Name}", nameof(AfterResponseAsync));
        return Task.CompletedTask;
    }

    private bool WarnNoSelect(SessionEventArgs session)
    {
        var request = session.HttpClient.Request;
        if (!ProxyUtils.IsGraphRequest(request) ||
            request.Method != "GET")
        {
            Logger.LogRequest("Not a Microsoft Graph GET request", MessageType.Skipped, new(session));
            return false;
        }

        var graphVersion = ProxyUtils.GetGraphVersion(request.RequestUri.AbsoluteUri);
        var tokenizedUrl = GetTokenizedUrl(request.RequestUri.AbsoluteUri);

        if (EndpointSupportsSelect(graphVersion, tokenizedUrl))
        {
            return !request.Url.Contains("$select", StringComparison.OrdinalIgnoreCase) &&
            !request.Url.Contains("%24select", StringComparison.OrdinalIgnoreCase);
        }
        else
        {
            Logger.LogRequest("Endpoint does not support $select", MessageType.Skipped, new(session));
            return false;
        }
    }

    private bool EndpointSupportsSelect(string graphVersion, string relativeUrl)
    {
        var fallback = relativeUrl.Contains("$value", StringComparison.OrdinalIgnoreCase);

        try
        {
            var dbConnection = MSGraphDbUtils.MSGraphDbConnection;
            // lookup information from the database
            var selectEndpoint = dbConnection.CreateCommand();
            selectEndpoint.CommandText = "SELECT hasSelect FROM endpoints WHERE path = @path AND graphVersion = @graphVersion";
            _ = selectEndpoint.Parameters.AddWithValue("@path", relativeUrl);
            _ = selectEndpoint.Parameters.AddWithValue("@graphVersion", graphVersion);
            var result = selectEndpoint.ExecuteScalar();
            var hasSelect = result != null && Convert.ToInt32(result, CultureInfo.InvariantCulture) == 1;
            return hasSelect;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error looking up endpoint in database");
            return fallback;
        }
    }

    private static string GetSelectParameterGuidanceUrl() => "https://aka.ms/devproxy/guidance/select";
    private static string BuildUseSelectMessage() =>
        $"To improve performance of your application, use the $select parameter. More info at {GetSelectParameterGuidanceUrl()}";

    private static string GetTokenizedUrl(string absoluteUrl)
    {
        var sanitizedUrl = ProxyUtils.SanitizeUrl(absoluteUrl);
        return "/" + string.Join("", new Uri(sanitizedUrl).Segments.Skip(2).Select(Uri.UnescapeDataString));
    }
}
