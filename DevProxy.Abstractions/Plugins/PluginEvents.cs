// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Titanium.Web.Proxy.Http;

namespace DevProxy.Abstractions.Plugins;

public class ThrottlerInfo(string throttlingKey, Func<Request, string, ThrottlingInfo> shouldThrottle, DateTime resetTime)
{
    /// <summary>
    /// Time when the throttling window will be reset
    /// </summary>
    public DateTime ResetTime { get; set; } = resetTime;
    /// <summary>
    /// Function responsible for matching the request to the throttling key.
    /// Takes as arguments:
    /// - intercepted request
    /// - the throttling key
    /// Returns an instance of ThrottlingInfo that contains information
    /// whether the request should be throttled or not.
    /// </summary>
    public Func<Request, string, ThrottlingInfo> ShouldThrottle { get; private set; } = shouldThrottle ?? throw new ArgumentNullException(nameof(shouldThrottle));
    /// <summary>
    /// Throttling key used to identify which requests should be throttled.
    /// Can be set to a hostname, full URL or a custom string value, that
    /// represents for example a portion of the API
    /// </summary>
    public string ThrottlingKey { get; private set; } = throttlingKey ?? throw new ArgumentNullException(nameof(throttlingKey));
}

public class ThrottlingInfo(int throttleForSeconds, string retryAfterHeaderName)
{
    public string RetryAfterHeaderName { get; set; } = retryAfterHeaderName ?? throw new ArgumentNullException(nameof(retryAfterHeaderName));
    public int ThrottleForSeconds { get; set; } = throttleForSeconds;
}
