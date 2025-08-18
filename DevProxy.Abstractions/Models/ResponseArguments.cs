namespace DevProxy.Abstractions.Models;
public class ResponseArguments(HttpRequestMessage httpRequestMessage, HttpResponseMessage httpResponseMessage, string requestId)
{
    public HttpRequestMessage HttpRequestMessage { get; } = httpRequestMessage;
    public HttpResponseMessage HttpResponseMessage { get; } = httpResponseMessage;
    public string RequestId { get; } = requestId ?? throw new ArgumentNullException(nameof(requestId));
}
