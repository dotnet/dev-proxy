// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Abstractions.Data;
using DevProxy.Abstractions.Proxy;
using DevProxy.Abstractions.Plugins;
using DevProxy.Abstractions.Utils;
using Microsoft.Extensions.Logging;
using System.Globalization;

namespace DevProxy.Plugins.Guidance;

public sealed class GraphSelectGuidancePlugin(
    ILogger<GraphSelectGuidancePlugin> logger,
    ISet<UrlToWatch> urlsToWatch,
    MSGraphDb msGraphDb) : BasePlugin(logger, urlsToWatch)
{
    private readonly MSGraphDb _msGraphDb = msGraphDb;

    public override string Name => nameof(GraphSelectGuidancePlugin);

    public override async Task InitializeAsync(InitArgs e, CancellationToken cancellationToken)
    {
        await base.InitializeAsync(e, cancellationToken);

        // let's not await so that it doesn't block the proxy startup
        _ = _msGraphDb.GenerateDbAsync(true, cancellationToken);
    }

    public override Func<RequestArguments, CancellationToken, Task>? ProvideRequestGuidanceAsync => (args, cancellationToken) =>
    {
        Logger.LogTrace("{Method} called", nameof(ProvideRequestGuidanceAsync));

        ArgumentNullException.ThrowIfNull(args);

        if (!ProxyUtils.MatchesUrlToWatch(UrlsToWatch, args.Request.RequestUri))
        {
            Logger.LogRequest("URL not matched", MessageType.Skipped, args.Request);
            return Task.CompletedTask;
        }
        if (args.Request.Method == HttpMethod.Options)
        {
            Logger.LogRequest("Skipping OPTIONS request", MessageType.Skipped, args.Request);
            return Task.CompletedTask;
        }

        if (WarnNoSelect(args.Request))
        {
            Logger.LogRequest(BuildUseSelectMessage(), MessageType.Warning, args.Request);
        }

        Logger.LogTrace("Left {Name}", nameof(ProvideRequestGuidanceAsync));
        return Task.CompletedTask;
    };

    private bool WarnNoSelect(HttpRequestMessage request)
    {
        if (!ProxyUtils.IsGraphRequest(request) ||
            request.Method != HttpMethod.Get)
        {
            Logger.LogRequest("Not a Microsoft Graph GET request", MessageType.Skipped, request);
            return false;
        }

        var graphVersion = ProxyUtils.GetGraphVersion(request.RequestUri!.AbsoluteUri);
        var tokenizedUrl = GetTokenizedUrl(request.RequestUri.AbsoluteUri);

        if (EndpointSupportsSelect(graphVersion, tokenizedUrl))
        {
            var url = request.RequestUri.AbsoluteUri;
            return !url.Contains("$select", StringComparison.OrdinalIgnoreCase) &&
            !url.Contains("%24select", StringComparison.OrdinalIgnoreCase);
        }
        else
        {
            Logger.LogRequest("Endpoint does not support $select", MessageType.Skipped, request);
            return false;
        }
    }

    private bool EndpointSupportsSelect(string graphVersion, string relativeUrl)
    {
        var fallback = relativeUrl.Contains("$value", StringComparison.OrdinalIgnoreCase);

        try
        {
            var dbConnection = _msGraphDb.Connection;
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
