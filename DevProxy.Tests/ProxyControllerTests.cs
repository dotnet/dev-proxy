// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using DevProxy.ApiControllers;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace DevProxy.Tests;

public sealed class ProxyControllerTests
{
    [Fact]
    public async Task RaiseMockRequestAsync_ReturnsNoContentAfterMockRequestCompletes()
    {
        var stateController = new FakeProxyStateController();
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(1));
        var controller = new ProxyController(stateController, new FakeProxyConfiguration(), cert);

        var result = await controller.RaiseMockRequestAsync(CancellationToken.None);

        _ = Assert.IsType<NoContentResult>(result);
        Assert.Equal(1, stateController.MockRequestCalls);
    }
}
