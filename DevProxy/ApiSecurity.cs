using DevProxy.State;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;

namespace DevProxy;

internal static class ApiSecurity
{
    internal const string TokenEnvironmentVariable = "DEV_PROXY_API_TOKEN";
    internal const int MinimumTokenLength = 32;

    private static readonly Lock TokenLock = new();
    private static string? _token;
    private static byte[]? _tokenBytes;
    private static string Token => EnsureToken().Token;
    private static byte[] TokenBytes => EnsureToken().TokenBytes;
    private static bool _tokenDisplayed;

    /// <summary>
    /// Resolves the API token from the value of <c>DEV_PROXY_API_TOKEN</c>.
    /// When the value is null or whitespace, a random token is generated.
    /// </summary>
    internal static bool TryResolveToken(string? environmentValue, out string token, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(environmentValue))
        {
            token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            return true;
        }

        token = environmentValue.Trim();
        if (token.TrimEnd('=').Length < MinimumTokenLength)
        {
            error = $"{TokenEnvironmentVariable} must be at least {MinimumTokenLength} characters long, not counting trailing = padding.";
            return false;
        }

        // RFC 6750 b64token characters, so the value can be sent as a Bearer credential
        if (!token.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '.' or '_' or '~' or '+' or '/' or '=') ||
            token.TrimEnd('=').Contains('=', StringComparison.Ordinal))
        {
            error = $"{TokenEnvironmentVariable} may contain only ASCII letters, digits, and the characters - . _ ~ + / (optionally followed by trailing = padding).";
            return false;
        }

        return true;
    }

    /// <summary>
    /// Resolves the API token from the current (effective) value of <c>DEV_PROXY_API_TOKEN</c>.
    /// Call after environment overrides are applied. Once resolved, the token is kept for the
    /// lifetime of the process, so it survives configuration reloads.
    /// </summary>
    public static bool TryInitializeToken(out string? error)
    {
        lock (TokenLock)
        {
            error = null;
            if (_token is not null)
            {
                return true;
            }

            if (!TryResolveToken(Environment.GetEnvironmentVariable(TokenEnvironmentVariable), out var token, out error))
            {
                return false;
            }

            _tokenBytes = Encoding.UTF8.GetBytes(token);
            _token = token;
            return true;
        }
    }

    private static (string Token, byte[] TokenBytes) EnsureToken()
    {
        if (!TryInitializeToken(out var error))
        {
            throw new InvalidOperationException(error);
        }

        return (_token!, _tokenBytes!);
    }

    public static bool ShouldDisplayToken => Environment.GetEnvironmentVariable("CI") is null;
    public static string? DisplayToken => ShouldDisplayToken ? Token : null;

    public static bool ShouldDisplayTokenFor(ProxyInstanceState state) =>
        ShouldDisplayToken && !state.SuppressTokenOutput;

    public static void LogTokenOnce(ILogger logger)
    {
        if (!ShouldDisplayToken || _tokenDisplayed)
        {
            return;
        }

        _tokenDisplayed = true;
        logger.LogInformation("API token: {ApiToken}", Token);
        logger.LogInformation("Send this token in the Authorization: Bearer header.");
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

    public static Task CheckOriginAsync(HttpContext context, Func<Task> next, string[] allowedOrigins)
    {
        context.Response.Headers.CacheControl = "no-store";
        if (context.Request.Headers.TryGetValue("Origin", out var origin) &&
            !allowedOrigins.Contains(origin.ToString(), StringComparer.Ordinal))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return Task.CompletedTask;
        }

        return next();
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

    public static Task AuthenticateAsync(HttpContext context, Func<Task> next)
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
            return Task.CompletedTask;
        }

        context.Response.Headers.CacheControl = "no-store";
        return next();
    }
}