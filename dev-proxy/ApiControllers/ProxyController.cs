// Copyright (c) .NET Foundation and Contributors. All Rights Reserved. Licensed under the MIT License (MIT). See License.md in the repository root for more information.

using Microsoft.AspNetCore.Mvc;
using Microsoft.DevProxy.Jwt;

namespace Microsoft.DevProxy.ApiControllers;

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
}
