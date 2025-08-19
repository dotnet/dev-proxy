namespace DevProxy.Abstractions.Plugins;
public class ResponseArguments(HttpRequestMessage request, HttpResponseMessage response, string requestId) : RequestArguments(request, requestId)
{
    public HttpResponseMessage Response { get; } = response;
}
