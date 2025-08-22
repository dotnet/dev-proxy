using Unobtanium.Web.Proxy;

namespace DevProxy.Proxy;

/// <summary>
/// <see cref="IProxyServerHttpClientFactory"/> for efficient re-use of available ports
/// </summary>
/// <param name="httpClientFactory">Is added to Dependency Injection</param>
internal sealed class EfficientProxyHttpClientFactory(IHttpClientFactory httpClientFactory) : IProxyHttpClientFactory
{
    internal const string HTTP_CLIENT_NAME = "DevProxy.Proxy.EfficientHttpClient";
    private readonly IHttpClientFactory _httpClientFactory = httpClientFactory;

    public HttpClient CreateHttpClient(string host) => _httpClientFactory.CreateClient(HTTP_CLIENT_NAME);
}
