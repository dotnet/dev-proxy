// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using DevProxy.ApiControllers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DevProxy.Tests;

public sealed class ProxyControllerTests : IDisposable
{
    private readonly X509Certificate2 _certificate;
    private readonly ServiceProvider _services;

    public ProxyControllerTests()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=Dev Proxy Test", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        _certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));

        var services = new ServiceCollection();
        _ = services.AddLogging();
        _ = services.AddControllers();
        _services = services.BuildServiceProvider();
    }

    public void Dispose()
    {
        _certificate.Dispose();
        _services.Dispose();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("crt")]
    [InlineData("CRT")]
    [InlineData("pem")]
    [InlineData("PEM")]
    public void GetRootCertificate_SupportedOrMissingFormat_ReturnsPem(string? format)
    {
        var controller = CreateController();

        var result = controller.GetRootCertificate(format);

        var file = Assert.IsType<FileContentResult>(result);
        Assert.Equal("application/x-x509-ca-cert", file.ContentType);
        var pem = Encoding.ASCII.GetString(file.FileContents);
        Assert.StartsWith("-----BEGIN CERTIFICATE-----", pem, StringComparison.Ordinal);
        Assert.EndsWith("-----END CERTIFICATE-----", pem, StringComparison.Ordinal);
        using var parsed = X509Certificate2.CreateFromPem(pem);
        Assert.Equal(_certificate.Thumbprint, parsed.Thumbprint);
    }

    [Theory]
    [InlineData("der")]
    [InlineData("cer")]
    public void GetRootCertificate_UnsupportedFormat_ReturnsValidationProblemListingFormats(string format)
    {
        var controller = CreateController();

        var result = controller.GetRootCertificate(format);

        var objectResult = Assert.IsAssignableFrom<ObjectResult>(result);
        var problem = Assert.IsType<ValidationProblemDetails>(objectResult.Value);
        Assert.Equal(StatusCodes.Status400BadRequest, problem.Status);
        var error = Assert.Single(problem.Errors["format"]);
        Assert.Contains("'crt'", error, StringComparison.Ordinal);
        Assert.Contains("'pem'", error, StringComparison.Ordinal);
        Assert.Contains("https://go.devproxy.net?go=api", error, StringComparison.Ordinal);
    }

    private ProxyController CreateController() =>
        new(new FakeProxyStateController(), new FakeProxyConfiguration(), _certificate)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { RequestServices = _services }
            }
        };

    [Fact]
    public async Task RaiseMockRequestAsync_ReturnsNoContentAfterMockRequestCompletes()
    {
        var stateController = new FakeProxyStateController();
        var controller = new ProxyController(stateController, new FakeProxyConfiguration(), _certificate);

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
