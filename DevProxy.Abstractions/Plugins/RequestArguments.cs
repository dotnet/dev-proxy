namespace DevProxy.Abstractions.Plugins;
/// <summary>
/// Represents the arguments for an HTTP request, including the request message and a unique request identifier.
/// </summary>
/// <remarks>This class encapsulates the HTTP request message and its associated identifier, ensuring that both
/// are provided and accessible. The <see cref="Request"/> property contains the HTTP request details, while the <see
/// cref="RequestId"/> property provides a unique identifier for tracking or logging purposes.</remarks>
/// <param name="request">The HTTP request message to be sent. This parameter cannot be <see langword="null"/>.</param>
/// <param name="requestId">A unique identifier for the request. This parameter cannot be <see langword="null"/>.</param>
public class RequestArguments(HttpRequestMessage request, string requestId)
{
    /// <summary>
    /// Incoming HTTP request message.
    /// </summary>
    public HttpRequestMessage Request { get; } = request;

    /// <summary>
    /// Request identifier.
    /// </summary>
    public RequestId RequestId { get; } = requestId ?? throw new ArgumentNullException(nameof(requestId));
}

/// <summary>
/// Represents a unique identifier for a request.
/// </summary>
/// <remarks>The <see cref="RequestId"/> type provides a strongly-typed representation of a request identifier, 
/// encapsulating a string value. It supports implicit conversions to and from <see cref="string"/>  for ease of use,
/// and ensures that the identifier is not null.</remarks>
/// <param name="Id"></param>
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