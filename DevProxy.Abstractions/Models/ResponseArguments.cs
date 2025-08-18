namespace DevProxy.Abstractions.Models;
public class ResponseArguments(HttpRequestMessage httpRequestMessage, HttpResponseMessage httpResponseMessage)
{
    public HttpRequestMessage HttpRequestMessage { get; } = httpRequestMessage;
    public HttpResponseMessage HttpResponseMessage { get; } = httpResponseMessage;
}
