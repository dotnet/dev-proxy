[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("DevProxy")]
namespace DevProxy.Abstractions.Plugins;

/// <summary>
/// If you need either global or request-specific storage, ask for this interface in your plugin.
/// </summary>
public interface IProxyStorage
{
    /// <summary>
    /// Access to global data shared across all requests.
    /// </summary>
    public Dictionary<string, object> GlobalData { get; }

    /// <summary>
    /// Get request-specific data by its ID.
    /// </summary>
    /// <param name="id"></param>
    /// <returns></returns>

    public Dictionary<string, object> GetRequestData(RequestId id);

    internal void RemoveRequestData(RequestId id);
}
