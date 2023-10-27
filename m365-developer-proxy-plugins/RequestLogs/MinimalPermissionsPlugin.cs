﻿// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.Extensions.Configuration;
using Microsoft365.DeveloperProxy.Abstractions;
using Microsoft365.DeveloperProxy.Plugins.RequestLogs.MinimalPermissions;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Microsoft365.DeveloperProxy.Plugins.RequestLogs;

internal class MinimalPermissionsPluginConfiguration
{
  [JsonPropertyName("type")]
  public PermissionsType Type { get; set; } = PermissionsType.Delegated;
}

public class MinimalPermissionsPlugin : BaseProxyPlugin
{
  public override string Name => nameof(MinimalPermissionsPlugin);
  private MinimalPermissionsPluginConfiguration _configuration = new();

  public override void Register(IPluginEvents pluginEvents,
                          IProxyContext context,
                          ISet<UrlToWatch> urlsToWatch,
                          IConfigurationSection? configSection = null)
  {
    base.Register(pluginEvents, context, urlsToWatch, configSection);

    configSection?.Bind(_configuration);

    pluginEvents.AfterRecordingStop += AfterRecordingStop;
  }
  private async void AfterRecordingStop(object? sender, RecordingArgs e)
  {
    if (!e.RequestLogs.Any())
    {
      return;
    }

    var methodAndUrlComparer = new MethodAndUrlComparer();
    var endpoints = new List<Tuple<string, string>>();

    foreach (var request in e.RequestLogs)
    {
      if (request.MessageType != MessageType.InterceptedRequest)
      {
        continue;
      }

      var methodAndUrlString = request.MessageLines.First();
      var methodAndUrl = GetMethodAndUrl(methodAndUrlString);

      var uri = new Uri(methodAndUrl.Item2);
      if (!ProxyUtils.IsGraphUrl(uri))
      {
        continue;
      }

      if (ProxyUtils.IsGraphBatchUrl(uri)) {
        var graphVersion = ProxyUtils.IsGraphBetaUrl(uri) ? "beta" : "v1.0";
        var requestsFromBatch = GetRequestsFromBatch(request.Context?.Session.HttpClient.Request.BodyString!, graphVersion, uri.Host);
        endpoints.AddRange(requestsFromBatch);
      }
      else {
        methodAndUrl = new Tuple<string, string>(methodAndUrl.Item1, GetTokenizedUrl(methodAndUrl.Item2));
        endpoints.Add(methodAndUrl);
      }
    }

    // Remove duplicates
    endpoints = endpoints.Distinct(methodAndUrlComparer).ToList();

    if (!endpoints.Any()) {
      _logger?.LogInfo("No requests to Microsoft Graph endpoints recorded. Will not retrieve minimal permissions.");
      return;
    }

    _logger?.LogInfo("Retrieving minimal permissions for:");
    _logger?.LogInfo(string.Join(Environment.NewLine, endpoints.Select(e => $"- {e.Item1} {e.Item2}")));
    _logger?.LogInfo("");

    _logger?.LogWarn("This plugin is in preview and may not return the correct results.");
    _logger?.LogWarn("Please review the permissions and test your app before using them in production.");
    _logger?.LogWarn("If you have any feedback, please open an issue at https://aka.ms/m365/proxy/issue.");
    _logger?.LogInfo("");

    await DetermineMinimalScopes(endpoints);
  }

  private Tuple<string, string>[] GetRequestsFromBatch(string batchBody, string graphVersion, string graphHostName)
  {
    var requests = new List<Tuple<string, string>>();

    if (String.IsNullOrEmpty(batchBody))
    {
      return requests.ToArray();
    }

    try {
      var batch = JsonSerializer.Deserialize<GraphBatchRequestPayload>(batchBody);
      if (batch == null)
      {
        return requests.ToArray();
      }

      foreach (var request in batch.Requests)
      {
        try {
          var method = request.Method;
          var url = request.Url;
          var absoluteUrl = $"https://{graphHostName}/{graphVersion}{url}";
          requests.Add(new Tuple<string, string>(method, GetTokenizedUrl(absoluteUrl)));
        }
        catch {}
      }
    }
    catch {}

    return requests.ToArray();
  }

  private string GetScopeTypeString()
  {
    return _configuration.Type switch
    {
      PermissionsType.Application => "Application",
      PermissionsType.Delegated => "DelegatedWork",
      _ => throw new InvalidOperationException($"Unknown scope type: {_configuration.Type}")
    };
  }

  private async Task DetermineMinimalScopes(IEnumerable<Tuple<string, string>> endpoints)
  {
    var payload = endpoints.Select(e => new RequestInfo { Method = e.Item1, Url = e.Item2 });

    try
    {
      var url = $"https://graphexplorerapi-staging.azurewebsites.net/permissions?scopeType={GetScopeTypeString()}";
      using (var client = new HttpClient())
      {
        var stringPayload = JsonSerializer.Serialize(payload);
        _logger?.LogDebug($"Calling {url} with payload{Environment.NewLine}{stringPayload}");

        var response = await client.PostAsJsonAsync(url, payload);
        var content = await response.Content.ReadAsStringAsync();

        _logger?.LogDebug($"Response:{Environment.NewLine}{content}");

        var resultsAndErrors = JsonSerializer.Deserialize<ResultsAndErrors>(content);
        var minimalScopes = resultsAndErrors?.Results?.Select(p => p.Value).ToArray() ?? Array.Empty<string>();
        var errors = resultsAndErrors?.Errors?.Select(e => $"- {e.Url} ({e.Message})") ?? Array.Empty<string>();
        if (minimalScopes.Any())
        {
          _logger?.LogInfo("Minimal permissions:");
          _logger?.LogInfo(string.Join(", ", minimalScopes));
          _logger?.LogInfo("");
        }
        if (errors.Any())
        {
          _logger?.LogError("Couldn't determine minimal permissions for the following URLs:");
          _logger?.LogError(string.Join(Environment.NewLine, errors));
        }
      }
    }
    catch (Exception ex)
    {
      _logger?.LogError($"An error has occurred while retrieving minimal permissions: {ex.Message}");
    }
  }

  private Tuple<string, string> GetMethodAndUrl(string message)
  {
    var info = message.Split(" ");
    if (info.Length > 2)
    {
      info = new[] { info[0], String.Join(" ", info.Skip(1)) };
    }
    return new Tuple<string, string>(info[0], info[1]);
  }

  private string GetTokenizedUrl(string absoluteUrl)
  {
    var sanitizedUrl = ProxyUtils.SanitizeUrl(absoluteUrl);
    return "/" + String.Join("", new Uri(sanitizedUrl).Segments.Skip(2).Select(s => Uri.UnescapeDataString(s)));
  }
}
