namespace DevProxy.Abstractions.Models;
public class RequestArguments(HttpRequestMessage request, string requestId)
{
    public HttpRequestMessage Request { get; } = request;
    public string RequestId { get; } = requestId ?? throw new ArgumentNullException(nameof(requestId));
}
