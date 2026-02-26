// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Logging;

#pragma warning disable IDE0130
namespace Microsoft.Extensions.Logging;
#pragma warning restore IDE0130

static class ILoggerExtensions
{
    public static IDisposable? BeginScope(this ILogger logger, string method, string url, int requestId) =>
      logger.BeginScope(new Dictionary<string, object>
      {
          { nameof(method), method },
          { nameof(url), url },
          { nameof(requestId), requestId }
      });

    /// <summary>
    /// Logs structured output (e.g., JSON) that should be written as-is
    /// without any log envelope formatting.
    /// </summary>
    public static void LogStructuredOutput(this ILogger logger, string message) =>
        logger.Log(LogLevel.Information, LogEvents.StructuredOutput, message, null, static (s, _) => s ?? string.Empty);
}