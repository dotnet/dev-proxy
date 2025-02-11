// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.CommandLine;
using System.CommandLine.Invocation;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Serialization;
using Microsoft.DevProxy.Abstractions.LanguageModel;
using Titanium.Web.Proxy;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Http;

namespace Microsoft.DevProxy.Abstractions;

public interface IProxyContext
{
    IProxyConfiguration Configuration { get; }
    X509Certificate2? Certificate { get; }
    ILanguageModelClient LanguageModelClient { get; }
}

public class ThrottlerInfo(string throttlingKey, Func<Request, string, ThrottlingInfo> shouldThrottle, DateTime resetTime)
{
    /// <summary>
    /// Throttling key used to identify which requests should be throttled.
    /// Can be set to a hostname, full URL or a custom string value, that
    /// represents for example a portion of the API
    /// </summary>
    public string ThrottlingKey { get; private set; } = throttlingKey ?? throw new ArgumentNullException(nameof(throttlingKey));
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
    /// Time when the throttling window will be reset
    /// </summary>
    public DateTime ResetTime { get; set; } = resetTime;
}

public class ThrottlingInfo(int throttleForSeconds, string retryAfterHeaderName)
{
    public int ThrottleForSeconds { get; set; } = throttleForSeconds;
    public string RetryAfterHeaderName { get; set; } = retryAfterHeaderName ?? throw new ArgumentNullException(nameof(retryAfterHeaderName));
}

public class ProxyEventArgsBase
{
    public Dictionary<string, object> SessionData { get; set; } = [];
    public Dictionary<string, object> GlobalData { get; set; } = [];
}

public class ProxyHttpEventArgsBase : ProxyEventArgsBase
{
    internal ProxyHttpEventArgsBase(SessionEventArgs session)
    {
        Session = session ?? throw new ArgumentNullException(nameof(session));
    }

    public SessionEventArgs Session { get; }

    public bool HasRequestUrlMatch(ISet<UrlToWatch> watchedUrls)
    {
        var match = watchedUrls.FirstOrDefault(r => r.Url.IsMatch(Session.HttpClient.Request.RequestUri.AbsoluteUri));
        return match is not null && !match.Exclude;
    }
}

public class ProxyRequestArgs(SessionEventArgs session, ResponseState responseState) : ProxyHttpEventArgsBase(session)
{
    public ResponseState ResponseState { get; } = responseState ?? throw new ArgumentNullException(nameof(responseState));

    public bool ShouldExecute(ISet<UrlToWatch> watchedUrls) =>
        !ResponseState.HasBeenSet
        && HasRequestUrlMatch(watchedUrls);
}

public class ProxyResponseArgs(SessionEventArgs session, ResponseState responseState) : ProxyHttpEventArgsBase(session)
{
    public ResponseState ResponseState { get; } = responseState ?? throw new ArgumentNullException(nameof(responseState));
}

public class InitArgs
{
    public InitArgs()
    {
    }
}

public class OptionsLoadedArgs(InvocationContext context, Option[] options)
{
    public InvocationContext Context { get; set; } = context ?? throw new ArgumentNullException(nameof(context));
    public Option[] Options { get; set; } = options ?? throw new ArgumentNullException(nameof(options));
}

public class RequestLog
{
    public string Message { get; set; }
    public MessageType MessageType { get; set; }
    [JsonIgnore]
    public LoggingContext? Context { get; set; }
    public string? Method { get; init; }
    public string? Url { get; init; }
    public string? PluginName { get; set; }

    public RequestLog(string message, MessageType messageType, LoggingContext? context) :
        this(message, messageType, context?.Session.HttpClient.Request.Method, context?.Session.HttpClient.Request.Url, context)
    {
    }

    public RequestLog(string message, MessageType messageType, string method, string url) :
        this(message, messageType, method, url, context: null)
    {
    }

    private RequestLog(string message, MessageType messageType, string? method, string? url, LoggingContext? context)
    {
        Message = message ?? throw new ArgumentNullException(nameof(message));
        MessageType = messageType;
        Context = context;
        Method = method;
        Url = url;
    }

    public void Deconstruct(out string message, out MessageType messageType, out LoggingContext? context, out string? method, out string? url)
    {
        message = Message;
        messageType = MessageType;
        context = Context;
        method = Method;
        url = Url;
    }
}

public class RecordingArgs(IEnumerable<RequestLog> requestLogs) : ProxyEventArgsBase
{
    public IEnumerable<RequestLog> RequestLogs { get; set; } = requestLogs ?? throw new ArgumentNullException(nameof(requestLogs));
}

public class RequestLogArgs(RequestLog requestLog)
{
    public RequestLog RequestLog { get; set; } = requestLog ?? throw new ArgumentNullException(nameof(requestLog));
}

public interface IPluginEvents
{
    /// <summary>
    /// Raised while starting the proxy, allows plugins to register command line options
    /// </summary>
    event EventHandler<InitArgs> Init;
    /// <summary>
    /// Raised during startup after command line arguments have been parsed,
    /// used to update the internal state of a plugin that registers command line options
    /// </summary>
    event EventHandler<OptionsLoadedArgs> OptionsLoaded;
    /// <summary>
    /// Raised before a request is sent to the server.
    /// Used to intercept requests.
    /// </summary>
    event AsyncEventHandler<ProxyRequestArgs> BeforeRequest;
    /// <summary>
    /// Raised after the response is received from the server.
    /// Is not raised if a response is set during the BeforeRequest event.
    /// Allows plugins to modify a response received from the server.
    /// </summary>
    event AsyncEventHandler<ProxyResponseArgs> BeforeResponse;
    /// <summary>
    /// Raised after a response is sent to the client.
    /// Raised for all responses
    /// </summary>
    event AsyncEventHandler<ProxyResponseArgs>? AfterResponse;
    /// <summary>
    /// Raised after request message has been logged.
    /// </summary>
    event AsyncEventHandler<RequestLogArgs>? AfterRequestLog;
    /// <summary>
    /// Raised after recording request logs has stopped.
    /// </summary>
    event AsyncEventHandler<RecordingArgs>? AfterRecordingStop;
    /// <summary>
    /// Raised when user requested issuing mock requests.
    /// </summary>
    event AsyncEventHandler<EventArgs>? MockRequest;

    void RaiseInit(InitArgs args);
    void RaiseOptionsLoaded(OptionsLoadedArgs args);
    Task RaiseProxyBeforeRequestAsync(ProxyRequestArgs args, ExceptionHandler? exceptionFunc = null);
    Task RaiseProxyBeforeResponseAsync(ProxyResponseArgs args, ExceptionHandler? exceptionFunc = null);
    Task RaiseProxyAfterResponseAsync(ProxyResponseArgs args, ExceptionHandler? exceptionFunc = null);
    Task RaiseRequestLoggedAsync(RequestLogArgs args, ExceptionHandler? exceptionFunc = null);
    Task RaiseRecordingStoppedAsync(RecordingArgs args, ExceptionHandler? exceptionFunc = null);
    Task RaiseMockRequestAsync(EventArgs args, ExceptionHandler? exceptionFunc = null);
}

public class PluginEvents : IPluginEvents
{
    /// <inheritdoc />
    public event EventHandler<InitArgs>? Init;
    /// <inheritdoc />
    public event EventHandler<OptionsLoadedArgs>? OptionsLoaded;
    /// <inheritdoc />
    public event AsyncEventHandler<ProxyRequestArgs>? BeforeRequest;
    /// <inheritdoc />
    public event AsyncEventHandler<ProxyResponseArgs>? BeforeResponse;
    /// <inheritdoc />
    public event AsyncEventHandler<ProxyResponseArgs>? AfterResponse;
    /// <inheritdoc />
    public event AsyncEventHandler<RequestLogArgs>? AfterRequestLog;
    /// <inheritdoc />
    public event AsyncEventHandler<RecordingArgs>? AfterRecordingStop;
    public event AsyncEventHandler<EventArgs>? MockRequest;

    public void RaiseInit(InitArgs args)
    {
        Init?.Invoke(this, args);
    }

    public void RaiseOptionsLoaded(OptionsLoadedArgs args)
    {
        OptionsLoaded?.Invoke(this, args);
    }

    public async Task RaiseProxyBeforeRequestAsync(ProxyRequestArgs args, ExceptionHandler? exceptionFunc = null)
    {
        if (BeforeRequest is not null)
        {
            await BeforeRequest.InvokeAsync(this, args, exceptionFunc);
        }
    }

    public async Task RaiseProxyBeforeResponseAsync(ProxyResponseArgs args, ExceptionHandler? exceptionFunc = null)
    {
        if (BeforeResponse is not null)
        {
            await BeforeResponse.InvokeAsync(this, args, exceptionFunc);
        }
    }

    public async Task RaiseProxyAfterResponseAsync(ProxyResponseArgs args, ExceptionHandler? exceptionFunc = null)
    {
        if (AfterResponse is not null)
        {
            await AfterResponse.InvokeAsync(this, args, exceptionFunc);
        }
    }

    public async Task RaiseRequestLoggedAsync(RequestLogArgs args, ExceptionHandler? exceptionFunc = null)
    {
        if (AfterRequestLog is not null)
        {
            await AfterRequestLog.InvokeAsync(this, args, exceptionFunc);
        }
    }

    public async Task RaiseRecordingStoppedAsync(RecordingArgs args, ExceptionHandler? exceptionFunc = null)
    {
        if (AfterRecordingStop is not null)
        {
            await AfterRecordingStop.InvokeAsync(this, args, exceptionFunc);
        }
    }

    public async Task RaiseMockRequestAsync(EventArgs args, ExceptionHandler? exceptionFunc = null)
    {
        if (MockRequest is not null)
        {
            await MockRequest.InvokeAsync(this, args, exceptionFunc);
        }
    }
}
