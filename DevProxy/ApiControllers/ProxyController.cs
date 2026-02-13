// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Microsoft.AspNetCore.Mvc;
using DevProxy.Jwt;
using System.Security.Cryptography.X509Certificates;
using System.ComponentModel.DataAnnotations;
using DevProxy.Proxy;
using DevProxy.Abstractions.Proxy;

namespace DevProxy.ApiControllers;

[ApiController]
[Route("[controller]")]
#pragma warning disable CA1515 // required for the API controller
public sealed class ProxyController(IProxyStateController proxyStateController, IProxyConfiguration proxyConfiguration, ILoggerFactory loggerFactory) : ControllerBase
#pragma warning restore CA1515
{
    private readonly IProxyStateController _proxyStateController = proxyStateController;
    private readonly IProxyConfiguration _proxyConfiguration = proxyConfiguration;
    private readonly ILoggerFactory _loggerFactory = loggerFactory;

    [HttpGet]
    public ProxyInfo Get() => ProxyInfo.From(_proxyStateController.ProxyState, _proxyConfiguration);

    [HttpPost]
    public async Task<IActionResult> SetAsync([FromBody] ProxyInfo proxyInfo, CancellationToken cancellationToken)
    {
        if (proxyInfo == null)
        {
            ModelState.AddModelError("ProxyInfo", "ProxyInfo cannot be null");
            return ValidationProblem(ModelState);
        }

        if (proxyInfo.ConfigFile != null)
        {
            ModelState.AddModelError("ConfigFile", "ConfigFile cannot be set");
            return ValidationProblem(ModelState);
        }

        if (proxyInfo.Recording.HasValue)
        {
            if (proxyInfo.Recording.Value)
            {
                _proxyStateController.StartRecording();
            }
            else
            {
                await _proxyStateController.StopRecordingAsync(cancellationToken);
            }
        }

        return Ok(ProxyInfo.From(_proxyStateController.ProxyState, _proxyConfiguration));
    }

    [HttpPost("mockRequest")]
#pragma warning disable CA1030
    public async Task RaiseMockRequestAsync(CancellationToken cancellationToken)
#pragma warning restore CA1030
    {
        await _proxyStateController.MockRequestAsync(cancellationToken);
        Response.StatusCode = 202;
    }

    [HttpPost("stopProxy")]
    public void StopProxy()
    {
        Response.StatusCode = 202;
        _proxyStateController.StopProxy();
    }

    [HttpPost("jwtToken")]
    public IActionResult CreateJwtToken([FromBody] JwtOptions jwtOptions)
    {
        if (jwtOptions == null)
        {
            var problemDetails = new ProblemDetails
            {
                Title = "Invalid Request",
                Detail = "JWT options cannot be null.",
                Status = StatusCodes.Status400BadRequest
            };
            return BadRequest(problemDetails);
        }

        if (jwtOptions.SigningKey != null && jwtOptions.SigningKey.Length < 32)
        {
            var problemDetails = new ProblemDetails
            {
                Title = "Key too short",
                Detail = "The specified signing key is too short. A signing key must be at least 32 characters.",
                Status = StatusCodes.Status400BadRequest
            };
            return BadRequest(problemDetails);
        }

        var token = JwtTokenGenerator.CreateToken(jwtOptions);

        return Ok(new JwtInfo { Token = token });
    }

    [HttpGet("rootCertificate")]
    public IActionResult GetRootCertificate([FromQuery][Required] string format)
    {
        if (string.IsNullOrWhiteSpace(format))
        {
            ModelState.AddModelError("format", "Format is required.");
            return ValidationProblem(ModelState);
        }

        if (!format.Equals("crt", StringComparison.OrdinalIgnoreCase))
        {
            ModelState.AddModelError("format", "Invalid format. Supported format is 'crt'.");
            return ValidationProblem(ModelState);
        }

        // Ensure ProxyServer is initialized with LoggerFactory for Unobtanium logging
        ProxyEngine.EnsureProxyServerInitialized(_loggerFactory);

        var certificate = ProxyEngine.ProxyServer.CertificateManager.RootCertificate;
        if (certificate == null)
        {
            var problemDetails = new ProblemDetails
            {
                Title = "Certificate Not Found",
                Detail = "No root certificate found.",
                Status = StatusCodes.Status404NotFound
            };
            return NotFound(problemDetails);
        }

        var certBytes = certificate.Export(X509ContentType.Cert);
        var base64Cert = Convert.ToBase64String(certBytes, Base64FormattingOptions.InsertLineBreaks);
        var pem = "-----BEGIN CERTIFICATE-----\n" + base64Cert + "\n-----END CERTIFICATE-----";
        var pemBytes = System.Text.Encoding.ASCII.GetBytes(pem);

        return File(pemBytes, "application/x-x509-ca-cert", "devProxy.pem");
    }
}
