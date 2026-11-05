// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
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

    private static ProxyController CreateController(FakeProxyStateController stateController, out X509Certificate2 cert)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        cert = request.CreateSelfSigned(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(1));
        return new ProxyController(stateController, new FakeProxyConfiguration(), cert);
    }

    [Fact]
    public async Task SetAsync_StopRecording_ReturnsReportFiles()
    {
        var stateController = new FakeProxyStateController
        {
            ReportFiles = ["/tmp/ExecutionSummaryPlugin_JsonReporter.json"]
        };
        var controller = CreateController(stateController, out var cert);
        using (cert)
        {
            var result = await controller.SetAsync(new ProxyInfo { Recording = false }, CancellationToken.None);

            var ok = Assert.IsType<OkObjectResult>(result);
            var info = Assert.IsType<ProxyInfo>(ok.Value);
            Assert.Equal(1, stateController.StopRecordingCalls);
            Assert.Equal(["/tmp/ExecutionSummaryPlugin_JsonReporter.json"], info.Reports);
        }
    }

    [Fact]
    public async Task SetAsync_StartRecording_OmitsReports()
    {
        var stateController = new FakeProxyStateController();
        var controller = CreateController(stateController, out var cert);
        using (cert)
        {
            var result = await controller.SetAsync(new ProxyInfo { Recording = true }, CancellationToken.None);

            var ok = Assert.IsType<OkObjectResult>(result);
            var info = Assert.IsType<ProxyInfo>(ok.Value);
            Assert.Null(info.Reports);
            Assert.DoesNotContain("reports", JsonSerializer.Serialize(info, JsonSerializerOptions.Web), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task SetAsync_WithReports_ReturnsValidationProblem()
    {
        var stateController = new FakeProxyStateController();
        var controller = CreateController(stateController, out var cert);
        using (cert)
        {
            var result = await controller.SetAsync(new ProxyInfo { Recording = false, Reports = ["x"] }, CancellationToken.None);

            _ = Assert.IsAssignableFrom<ObjectResult>(result);
            Assert.Equal(0, stateController.StopRecordingCalls);
        }
    }
}
