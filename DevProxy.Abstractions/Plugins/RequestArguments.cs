namespace DevProxy.Abstractions.Plugins;
public class RequestArguments(HttpRequestMessage request, string requestId)
{
    public HttpRequestMessage Request { get; } = request;
    public RequestId RequestId { get; } = requestId ?? throw new ArgumentNullException(nameof(requestId));
}

public record RequestId(string Id)
{
    private string Id { get; } = Id ?? throw new ArgumentNullException(nameof(Id));
    public static implicit operator string(RequestId requestId)
    {
        ArgumentNullException.ThrowIfNull(requestId);
        return requestId.Id;
    }

    public static implicit operator RequestId(string id)
    {
        return new(id);
    }

    public static RequestId FromString(string id)
    {
        return new RequestId(id);
    }
}