// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

#pragma warning disable IDE0130
namespace Microsoft.Extensions.Logging;
#pragma warning restore IDE0130

public static class ILoggerExtensions
{
    public static IDisposable? BeginScope(this ILogger logger, string method, string url, int requestId) =>
      logger.BeginScope(new Dictionary<string, object>
      {
          { nameof(method), method },
          { nameof(url), url },
          { nameof(requestId), requestId }
      });
}