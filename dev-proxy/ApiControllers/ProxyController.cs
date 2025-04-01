// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Microsoft.AspNetCore.Mvc;
using DevProxy.Jwt;
using System.Security.Cryptography.X509Certificates;
using System.ComponentModel.DataAnnotations;

namespace DevProxy.ApiControllers;

[ApiController]
[Route("[controller]")]
public class ProxyController(IProxyState proxyState) : ControllerBase
{
    private readonly IProxyState _proxyState = proxyState;

    [HttpGet]
    public ProxyInfo Get() => ProxyInfo.From(_proxyState);

    [HttpPost]
    public async Task<IActionResult> SetAsync([FromBody] ProxyInfo proxyInfo)
    {
        if (proxyInfo.ConfigFile != null)
        {
            return BadRequest("ConfigFile cannot be set");
        }

        if (proxyInfo.Recording.HasValue)
        {
            if (proxyInfo.Recording.Value)
            {
                _proxyState.StartRecording();
            }
            else
            {
                await _proxyState.StopRecordingAsync();
            }
        }

        return Ok(ProxyInfo.From(_proxyState));
    }

    [HttpPost("raiseMockRequest")]
    public async Task RaiseMockRequestAsync()
    {
        await _proxyState.RaiseMockRequestAsync();
        Response.StatusCode = 202;
    }

    [HttpPost("stopProxy")]
    public void StopProxy()
    {
        Response.StatusCode = 202;
        _proxyState.StopProxy();
    }

    [HttpPost("createJwtToken")]
    public IActionResult CreateJwtToken([FromBody] JwtOptions jwtOptions)
    {
        if (jwtOptions.SigningKey != null && jwtOptions.SigningKey.Length < 32)
        {
            return BadRequest("The specified signing key is too short. A signing key must be at least 32 characters.");
        }

        var token = JwtTokenGenerator.CreateToken(jwtOptions);

        return Ok(new JwtInfo { Token = token });
    }

    [HttpGet("rootCertificate")]
    public IActionResult GetRootCertificate([FromQuery][Required] string format)
    {
        if (!format.Equals("crt", StringComparison.OrdinalIgnoreCase))
        {
            ModelState.AddModelError("format", "Invalid format. Supported format is 'crt'.");
            return ValidationProblem(ModelState);
        }

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
