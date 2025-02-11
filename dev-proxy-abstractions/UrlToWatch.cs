using System.Text.RegularExpressions;

namespace DevProxy.Abstractions;

public class UrlToWatch(Regex url, bool exclude = false)
{
    public bool Exclude { get; } = exclude;
    public Regex Url { get; } = url;
}