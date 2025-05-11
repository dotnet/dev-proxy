using System.Text.Json;
using DevProxy.Abstractions.Proxy;

#pragma warning disable IDE0130
namespace Microsoft.Extensions.Logging;
#pragma warning restore IDE0130

public static class ILoggerExtensions
{
    public static void LogRequest(this ILogger logger, string message, MessageType messageType, LoggingContext? context = null)
    {
        logger.Log(new RequestLog(message, messageType, context));
    }

    public static void LogRequest(this ILogger logger, string message, MessageType messageType, string method, string url)
    {
        logger.Log(new RequestLog(message, messageType, method, url));
    }

    public static void Log(this ILogger logger, RequestLog message)
    {
        ArgumentNullException.ThrowIfNull(logger);

        logger.Log(LogLevel.Information, 0, message, exception: null, (m, _) => JsonSerializer.Serialize(m));
    }
}