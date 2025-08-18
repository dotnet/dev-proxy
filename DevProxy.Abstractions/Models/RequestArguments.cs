namespace DevProxy.Abstractions.Models;
public class RequestArguments(HttpRequestMessage request)
{
    public HttpRequestMessage Request { get; } = request;
}
