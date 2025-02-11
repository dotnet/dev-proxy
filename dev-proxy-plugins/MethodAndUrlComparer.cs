namespace DevProxy.Plugins;

internal class MethodAndUrlComparer : IEqualityComparer<(string method, string url)>
{
    public bool Equals((string method, string url) x, (string method, string url) y)
    {
        return x.method == y.method && x.url == y.url;
    }

    public int GetHashCode((string method, string url) obj)
    {
        int methodHashCode = obj.method.GetHashCode();
        int urlHashCode = obj.url.GetHashCode();

        return methodHashCode ^ urlHashCode;
    }
}