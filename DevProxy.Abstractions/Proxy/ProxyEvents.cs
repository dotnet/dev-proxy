// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Abstractions.Utils;
using System.CommandLine;
using System.Text.Json.Serialization;
using Titanium.Web.Proxy.EventArguments;

namespace DevProxy.Abstractions.Proxy;

public class ProxyEventArgsBase
{
    public Dictionary<string, object> SessionData { get; init; } = [];
    public Dictionary<string, object> GlobalData { get; init; } = [];
}

public class ProxyHttpEventArgsBase(SessionEventArgs session) : ProxyEventArgsBase
{
    public SessionEventArgs Session { get; } = session ??
        throw new ArgumentNullException(nameof(session));

    public bool HasRequestUrlMatch(ISet<UrlToWatch> watchedUrls) =>
        ProxyUtils.MatchesUrlToWatch(watchedUrls, Session.HttpClient.Request.RequestUri.AbsoluteUri);
}

public class ProxyRequestArgs(SessionEventArgs session, ResponseState responseState) :
    ProxyHttpEventArgsBase(session)
{
    public ResponseState ResponseState { get; } = responseState ??
        throw new ArgumentNullException(nameof(responseState));

    public bool ShouldExecute(ISet<UrlToWatch> watchedUrls) =>
        !ResponseState.HasBeenSet
        && HasRequestUrlMatch(watchedUrls);
}

public class ProxyResponseArgs(SessionEventArgs session, ResponseState responseState) :
    ProxyHttpEventArgsBase(session)
{
    public ResponseState ResponseState { get; } = responseState ??
        throw new ArgumentNullException(nameof(responseState));
}

public class InitArgs
{
    public required IServiceProvider ServiceProvider { get; init; }
}

public class OptionsLoadedArgs(ParseResult parseResult)
{
    public ParseResult ParseResult { get; set; } = parseResult ??
        throw new ArgumentNullException(nameof(parseResult));
}

public class RequestLog
{
    [JsonIgnore]
    public LoggingContext? Context { get; set; }
    public string Message { get; set; }
    public MessageType MessageType { get; set; }
    public string? Method { get; init; }
    public string? PluginName { get; set; }
    public string? Url { get; init; }

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
    public IEnumerable<RequestLog> RequestLogs { get; set; } = requestLogs ??
        throw new ArgumentNullException(nameof(requestLogs));
}

public class RequestLogArgs(RequestLog requestLog)
{
    public RequestLog RequestLog { get; set; } = requestLog ??
        throw new ArgumentNullException(nameof(requestLog));
}
