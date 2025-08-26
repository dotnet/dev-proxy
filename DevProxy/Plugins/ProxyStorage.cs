using DevProxy.Abstractions.Plugins;
using DevProxy.Proxy;
using System.Collections.Concurrent;

namespace DevProxy.Plugins;

/// <summary>
/// Default implementation of <see cref="IProxyStorage"/>.
/// </summary>
internal class ProxyStorage : IProxyStorage
{
    internal ProxyStorage(IProxyState proxyState)
    {
        GlobalData = proxyState.GlobalData ?? throw new ArgumentException("GlobalData cannot be null.", nameof(proxyState));
    }
    public Dictionary<string, object> GlobalData { get; private set; }

    //Dictionary<string, object> IProxyStorage.GlobalData => throw new NotImplementedException();

    public Dictionary<string, object> GetRequestData(RequestId id) => _requestData.TryGetValue(id, out var data) ? data : [];
    public void RemoveRequestData(RequestId id) => _requestData.Remove(id, out _);

    private readonly ConcurrentDictionary<RequestId, Dictionary<string, object>> _requestData = [];
}
