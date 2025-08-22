namespace DevProxy.Abstractions.Plugins;
public class PluginResponse
{
    public HttpRequestMessage? Request { get; private set; }
    public HttpResponseMessage? Response { get; private set; }
    private PluginResponse(HttpResponseMessage? response, HttpRequestMessage? request)
    {
        Response = response;
        Request = request;
    }

    public static PluginResponse Continue() => new(null, null);
    public static PluginResponse Continue(HttpRequestMessage request) => new(null, request);
    public static PluginResponse Respond(HttpResponseMessage response) => new(response, null);
}
