// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Abstractions.Models;
using DevProxy.Abstractions.Proxy;
using DevProxy.Abstractions.Utils;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Web;

namespace DevProxy.Plugins.Mocking;

sealed class IdToken
{
    public string? Aud { get; set; }
    public int? Exp { get; set; }
    public int? Iat { get; set; }
    public string? Iss { get; set; }
    public string? Name { get; set; }
    public int? Nbf { get; set; }
    public string? Nonce { get; set; }
    public string? Oid { get; set; }
    [JsonPropertyName("preferred_username")]
    public string? PreferredUsername { get; set; }
    public string? Rh { get; set; }
    public string? Sub { get; set; }
    public string? Tid { get; set; }
    public string? Uti { get; set; }
    public string? Ver { get; set; }
}

public sealed class EntraMockResponsePlugin(
    ILogger<EntraMockResponsePlugin> logger,
    ISet<UrlToWatch> urlsToWatch,
    X509Certificate2 certificate,
    IProxyConfiguration proxyConfiguration,
    IConfigurationSection pluginConfigurationSection) :
    MockResponsePlugin(
        logger,
        urlsToWatch,
        proxyConfiguration,
        pluginConfigurationSection)
{
    private string? lastNonce;

    public override string Name => nameof(EntraMockResponsePlugin);

    // Running on POST requests with a body
    protected override void ProcessMockResponse(ref byte[] body, IList<MockResponseHeader> headers, ProxyRequestArgs e, MockResponse? matchingResponse)
    {
        ArgumentNullException.ThrowIfNull(e);

        base.ProcessMockResponse(ref body, headers, e, matchingResponse);

        var bodyString = Encoding.UTF8.GetString(body);
        var changed = false;

        StoreLastNonce(e);
        UpdateMsalStateInBody(ref bodyString, e, ref changed);
        UpdateIdToken(ref bodyString, ref changed);
        UpdateDevProxyKeyId(ref bodyString, ref changed);
        UpdateDevProxyCertificateChain(ref bodyString, ref changed);

        if (changed)
        {
            body = Encoding.UTF8.GetBytes(bodyString);
        }
    }

    // Running on GET requests without a body
    protected override void ProcessMockResponse(ref string? body, IList<MockResponseHeader> headers, ProxyRequestArgs e, MockResponse? matchingResponse)
    {
        base.ProcessMockResponse(ref body, headers, e, matchingResponse);

        StoreLastNonce(e);
        UpdateMsalStateInHeaders(headers, e);
    }

    private void UpdateDevProxyCertificateChain(ref string bodyString, ref bool changed)
    {
        if (!bodyString.Contains("@dynamic.devProxyCertificateChain", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var certificateChain = GetCertificateChain().First();
        bodyString = bodyString.Replace("@dynamic.devProxyCertificateChain", certificateChain, StringComparison.OrdinalIgnoreCase);
        changed = true;
    }

    private void UpdateDevProxyKeyId(ref string bodyString, ref bool changed)
    {
        if (!bodyString.Contains("@dynamic.devProxyKeyId", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        bodyString = bodyString.Replace("@dynamic.devProxyKeyId", GetKeyId(), StringComparison.OrdinalIgnoreCase);
        changed = true;
    }

    private void StoreLastNonce(ProxyRequestArgs e)
    {
        if (e.Session.HttpClient.Request.RequestUri.Query.Contains("nonce=", StringComparison.OrdinalIgnoreCase))
        {
            var queryString = HttpUtility.ParseQueryString(e.Session.HttpClient.Request.RequestUri.Query);
            lastNonce = queryString["nonce"];
        }
    }

    private void UpdateIdToken(ref string body, ref bool changed)
    {
        if ((!body.Contains("id_token\":\"@dynamic", StringComparison.OrdinalIgnoreCase) &&
            !body.Contains("id_token\": \"@dynamic", StringComparison.OrdinalIgnoreCase)) ||
            string.IsNullOrEmpty(lastNonce))
        {
            return;
        }

        var idTokenRegex = new Regex("id_token\":\\s?\"([^\"]+)\"");

        var idToken = idTokenRegex.Match(body).Groups[1].Value;
        idToken = idToken.Replace("@dynamic.", "", StringComparison.OrdinalIgnoreCase);
        var tokenChunks = idToken.Split('.');
        // base64 decode the second chunk from the array
        // before decoding, we need to pad the base64 to a multiple of 4
        // or Convert.FromBase64String will throw an exception
        var decodedToken = Encoding.UTF8.GetString(Convert.FromBase64String(PadBase64(tokenChunks[1])));
        var token = JsonSerializer.Deserialize<IdToken>(decodedToken, ProxyUtils.JsonSerializerOptions);
        if (token is null)
        {
            return;
        }

        token.Nonce = lastNonce;

        tokenChunks[1] = Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(token, ProxyUtils.JsonSerializerOptions)));
        body = idTokenRegex.Replace(body, $"id_token\":\"{string.Join('.', tokenChunks)}\"");
        changed = true;
    }

    private string GetKeyId() => certificate.Thumbprint ?? "";

    private List<string> GetCertificateChain()
    {
        if (certificate is null)
        {
            return [];
        }

        var collection = new X509Certificate2Collection
        {
            certificate
        };

        var certificateChain = new List<string>();
        foreach (var certificate in collection)
        {
            var base64String = Convert.ToBase64String(certificate.RawData);
            certificateChain.Add(base64String);
        }

        return certificateChain;
    }

    private static string PadBase64(string base64)
    {
        var padding = new string('=', (4 - (base64.Length % 4)) % 4);
        return base64 + padding;
    }

    private static void UpdateMsalStateInHeaders(IList<MockResponseHeader> headers, ProxyRequestArgs e)
    {
        var locationHeader = headers.FirstOrDefault(h => h.Name.Equals("Location", StringComparison.OrdinalIgnoreCase));

        if (locationHeader is null ||
            !e.Session.HttpClient.Request.RequestUri.Query.Contains("state=", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var queryString = HttpUtility.ParseQueryString(e.Session.HttpClient.Request.RequestUri.Query);
        var msalState = queryString["state"];
        locationHeader.Value = locationHeader.Value.Replace("state=@dynamic", $"state={msalState}", StringComparison.OrdinalIgnoreCase);
    }

    private static void UpdateMsalStateInBody(ref string body, ProxyRequestArgs e, ref bool changed)
    {
        if (!body.Contains("state=@dynamic", StringComparison.OrdinalIgnoreCase) ||
          !e.Session.HttpClient.Request.RequestUri.Query.Contains("state=", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var queryString = HttpUtility.ParseQueryString(e.Session.HttpClient.Request.RequestUri.Query);
        var msalState = queryString["state"];
        body = body.Replace("state=@dynamic", $"state={msalState}", StringComparison.OrdinalIgnoreCase);
        changed = true;
    }
}