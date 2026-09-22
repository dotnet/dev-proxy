using DevProxy.State;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;

namespace DevProxy;

internal static class ApiSecurity
{
    private static readonly string Token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    private static readonly byte[] TokenBytes = Encoding.UTF8.GetBytes(Token);
    private static bool _tokenDisplayed;

    public static bool ShouldDisplayToken => Environment.GetEnvironmentVariable("CI") is null;
    public static string? DisplayToken => ShouldDisplayToken ? Token : null;

    public static void PrintTokenOnce(string apiUrl)
    {
        if (!ShouldDisplayToken || _tokenDisplayed)
        {
            return;
        }

        _tokenDisplayed = true;
        Console.WriteLine();
        Console.WriteLine($"API URL:   {apiUrl}");
        Console.WriteLine($"API token: {Token}");
        Console.WriteLine("Send this token in the Authorization: Bearer header.");
    }

    public static string[] GetAllowedOrigins(IConfiguration configuration)
    {
        var origins = configuration.GetSection("apiAllowedOrigins").Get<string[]>() ?? [];
        foreach (var origin in origins)
        {
            if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
                uri.UserInfo.Length != 0 || origin.Contains('*', StringComparison.Ordinal) ||
                !string.Equals(origin, uri.GetLeftPart(UriPartial.Authority), StringComparison.Ordinal))
            {
                throw new InvalidOperationException("apiAllowedOrigins must contain exact HTTP or HTTPS origins without paths, wildcards, or trailing slashes.");
            }
        }

        return origins;
    }

    public static async Task CheckOriginAsync(HttpContext context, Func<Task> next, string[] allowedOrigins)
    {
        context.Response.Headers.CacheControl = "no-store";
        if (context.Request.Headers.TryGetValue("Origin", out var origin) &&
            !allowedOrigins.Contains(origin.ToString(), StringComparer.Ordinal))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        await next();
    }

    public static string GetTokenFilePath(int pid) =>
        Path.Combine(StateManager.GetConfigFolder(), "credentials", $"api-{pid}.token");

    public static string GetApiUrl(Microsoft.AspNetCore.Hosting.Server.IServer server, int fallbackPort) =>
        GetApiUrl(server.Features.Get<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>()?.Addresses.FirstOrDefault()
            ?? $"http://127.0.0.1:{fallbackPort}");

    public static string GetApiUrl(string url)
    {
        var address = new UriBuilder(url);
        address.Host = address.Uri.IdnHost switch
        {
            "0.0.0.0" => "127.0.0.1",
            "::" => "::1",
            _ => address.Host
        };
        return address.Uri.GetLeftPart(UriPartial.Authority);
    }

    public static Task SaveTokenAsync(CancellationToken cancellationToken = default)
    {
        PrivateFiles.EnsureDirectory(StateManager.GetConfigFolder());
        PrivateFiles.EnsureDirectory(Path.GetDirectoryName(GetTokenFilePath(Environment.ProcessId))!, secureExisting: true);
        return PrivateFiles.WriteAllTextAsync(GetTokenFilePath(Environment.ProcessId), Token, cancellationToken);
    }

    public static async Task<HttpClient> CreateClientAsync(ProxyInstanceState state, TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(state.ApiUrl, UriKind.Absolute, out var address) ||
            address.Scheme != Uri.UriSchemeHttp ||
            !System.Net.IPAddress.TryParse(address.IdnHost, out _))
        {
            throw new InvalidOperationException("The Dev Proxy API address must be an HTTP IP address.");
        }

        var token = (await File.ReadAllTextAsync(GetTokenFilePath(state.Pid), cancellationToken)).Trim();
        var authorization = new AuthenticationHeaderValue("Bearer", token);
#pragma warning disable CA2000
        var handler = new HttpClientHandler
        {
            UseProxy = false,
            AllowAutoRedirect = false,
            CheckCertificateRevocationList = true
        };
#pragma warning restore CA2000
        try
        {
            var client = new HttpClient(handler, disposeHandler: true)
            {
                BaseAddress = address,
                Timeout = timeout
            };
            client.DefaultRequestHeaders.Authorization = authorization;
            return client;
        }
        catch
        {
            handler.Dispose();
            throw;
        }
    }

    public static async Task AuthenticateAsync(HttpContext context, Func<Task> next)
    {
        var authorization = context.Request.Headers.Authorization.ToString();
        const string prefix = "Bearer ";
        if (authorization.Length != prefix.Length + Token.Length ||
            !authorization.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
            !CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(authorization[prefix.Length..]), TokenBytes))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            context.Response.Headers.WWWAuthenticate = "Bearer";
            return;
        }

        context.Response.Headers.CacheControl = "no-store";
        await next();
    }
}