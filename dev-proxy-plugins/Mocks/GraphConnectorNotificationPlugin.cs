// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.DevProxy.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using Titanium.Web.Proxy.EventArguments;

namespace Microsoft.DevProxy.Plugins.Mocks;

public class GraphConnectorNotificationConfiguration : MockRequestConfiguration
{
    public string? Audience { get; set; }
    public string? Tenant { get; set; }
}

public class GraphConnectorNotificationPlugin(IPluginEvents pluginEvents, IProxyContext context, ILogger logger, ISet<UrlToWatch> urlsToWatch, IConfigurationSection? configSection = null) : MockRequestPlugin(pluginEvents, context, logger, urlsToWatch, configSection)
{
    private string? _ticket = null;
    private readonly GraphConnectorNotificationConfiguration _graphConnectorConfiguration = new();

    public override string Name => nameof(GraphConnectorNotificationPlugin);

    public override async Task RegisterAsync()
    {
        await base.RegisterAsync();
        ConfigSection?.Bind(_graphConnectorConfiguration);
        _graphConnectorConfiguration.MockFile = _configuration.MockFile;
        _graphConnectorConfiguration.Request = _configuration.Request;
        
        PluginEvents.BeforeRequest += OnBeforeRequestAsync;
    }

    private Task OnBeforeRequestAsync(object sender, ProxyRequestArgs e)
    {
        if (!ProxyUtils.IsGraphRequest(e.Session.HttpClient.Request))
        {
            Logger.LogRequest("Request is not a Microsoft Graph request", MessageType.Skipped, new LoggingContext(e.Session));
            return Task.CompletedTask;
        }

        VerifyTicket(e.Session);
        return Task.CompletedTask;
    }

    private void VerifyTicket(SessionEventArgs session)
    {
        if (_ticket is null)
        {
            Logger.LogRequest("No ticket found in the Graph request", MessageType.Skipped, new LoggingContext(session));
            return;
        }

        var request = session.HttpClient.Request;

        if (request.Method != "POST" && request.Method != "DELETE")
        {
            Logger.LogRequest("Skipping non-POST and -DELETE request", MessageType.Skipped, new LoggingContext(session));
            return;
        }

        if ((request.Method == "POST" &&
            !request.RequestUri.AbsolutePath.EndsWith("/external/connections", StringComparison.OrdinalIgnoreCase)) ||
            (request.Method == "DELETE" &&
            !request.RequestUri.AbsolutePath.Contains("/external/connections/", StringComparison.OrdinalIgnoreCase)))
        {
            Logger.LogRequest("Skipping non-connection request", MessageType.Skipped, new LoggingContext(session));
            return;
        }

        var ticketFromHeader = request.Headers.FirstOrDefault(h => h.Name.Equals("GraphConnectors-Ticket", StringComparison.OrdinalIgnoreCase))?.Value;
        if (ticketFromHeader is null)
        {
            Logger.LogRequest("No ticket header found in the Graph connector notification", MessageType.Failed, new LoggingContext(session));
            return;
        }

        if (ticketFromHeader != _ticket)
        {
            Logger.LogRequest($"Ticket on the request does not match the expected ticket. Expected: {_ticket}. Request: {ticketFromHeader}", MessageType.Failed, new LoggingContext(session));
        }
        else
        {
            Logger.LogRequest("Ticket verified", MessageType.Normal, new LoggingContext(session));
        }
    }

    protected override async Task OnMockRequestAsync(object sender, EventArgs e)
    {
        if (_configuration.Request is null)
        {
            Logger.LogDebug("No mock request is configured. Skipping.");
            return;
        }

        using var httpClient = new HttpClient();
        var requestMessage = GetRequestMessage();
        if (requestMessage.Content is null)
        {
            Logger.LogError("No body found in the mock request. Skipping.");
            return;
        }
        var requestBody = await requestMessage.Content.ReadAsStringAsync();
        requestBody = requestBody.Replace("@dynamic.validationToken", GetJwtToken());
        requestMessage.Content = new StringContent(requestBody, Encoding.UTF8, "application/json");
        LoadTicket();

        try
        {
            Logger.LogRequest("Sending Graph connector notification", MessageType.Mocked, _configuration.Request.Method, _configuration.Request.Url);

            var response = await httpClient.SendAsync(requestMessage);

            if (response.StatusCode != HttpStatusCode.Accepted)
            {
                Logger.LogRequest($"Incorrect response status code {(int)response.StatusCode} {response.StatusCode}. Expected: 202 Accepted", MessageType.Failed, _configuration.Request.Method, _configuration.Request.Url);
            }

            if (response.Content is not null)
            {
                var content = await response.Content.ReadAsStringAsync();
                if (!string.IsNullOrEmpty(content))
                {
                    Logger.LogRequest("Received response body while empty response expected", MessageType.Failed, _configuration.Request.Method, _configuration.Request.Url);
                }
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "An error has occurred while sending the Graph connector notification to {url}", _configuration.Request.Url);
        }
    }

    private string GetJwtToken()
    {
        var signingCredentials = new X509SigningCredentials(Context.Certificate, SecurityAlgorithms.RsaSha256);

        var tokenHandler = new JwtSecurityTokenHandler();
        var tokenDescriptor = new SecurityTokenDescriptor
        {
            Claims = new Dictionary<string, object>
            {
                // Microsoft Graph Change Tracking
                { "azp", "0bf30f3b-4a52-48df-9a82-234910c4a086" },
                // client cert auth
                { "azpacr", "2" },
                { "tid", _graphConnectorConfiguration.Tenant ?? "" },
                { "ver", "2.0" }
                
            },
            Expires = DateTime.UtcNow.AddMinutes(60),
            Issuer = $"https://login.microsoftonline.com/{_graphConnectorConfiguration.Tenant}/v2.0",
            Audience = _graphConnectorConfiguration.Audience,
            SigningCredentials = signingCredentials
        };

        var token = tokenHandler.CreateToken(tokenDescriptor);
        return tokenHandler.WriteToken(token);
    }

    private void LoadTicket()
    {
        if (_ticket is not null)
        {
            return;
        }

        if (_configuration.Request?.Body is null)
        {
            Logger.LogWarning("No body found in the Graph connector notification. Ticket will not be loaded.");
            return;
        }

        try
        {
            var body = (JsonElement)_configuration.Request.Body;
            _ticket = body.Get("value")?.Get(0)?.Get("resourceData")?.Get("connectorsTicket")?.GetString();

            if (string.IsNullOrEmpty(_ticket))
            {
                Logger.LogError("No ticket found in the Graph connector notification body");
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "An error has occurred while reading the ticket from the Graph connector notification body");
        }
    }
}
