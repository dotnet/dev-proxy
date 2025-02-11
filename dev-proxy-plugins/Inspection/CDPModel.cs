// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Text.Json.Serialization;
using Microsoft.DevProxy.Abstractions;

// CDP = Chrome DevTools Protocol
#pragma warning disable IDE0130
namespace Microsoft.DevProxy.Plugins.Inspection.CDP;
#pragma warning restore IDE0130

public class RequestWillBeSentExtraInfoMessage : Message<RequestWillBeSentExtraInfoParams>
{
    public RequestWillBeSentExtraInfoMessage()
    {
        Method = "Network.requestWillBeSentExtraInfo";
    }
}

public class RequestWillBeSentExtraInfoParams
{
    [JsonPropertyName("requestId")]
    public string? RequestId { get; set; }

    [JsonPropertyName("associatedCookies")]
    public object[]? AssociatedCookies { get; set; }

    [JsonPropertyName("headers")]
    public Dictionary<string, string>? Headers { get; set; }
}

public class RequestWillBeSentMessage : Message<RequestWillBeSentParams>
{
    public RequestWillBeSentMessage()
    {
        Method = "Network.requestWillBeSent";
    }
}

public class RequestWillBeSentParams
{
    [JsonPropertyName("requestId")]
    public string? RequestId { get; set; }
    [JsonPropertyName("loaderId")]
    public string? LoaderId { get; set; }
    [JsonPropertyName("documentURL")]
    public string? DocumentUrl { get; set; }
    [JsonPropertyName("request")]
    public Request? Request { get; set; }
    [JsonPropertyName("timestamp")]
    public double? Timestamp { get; set; }
    [JsonPropertyName("wallTime")]
    public long? WallTime { get; set; }
    [JsonPropertyName("initiator")]
    public Initiator? Initiator { get; set; }
}

public class ResponseReceivedMessage : Message<ResponseReceivedParams>
{
    public ResponseReceivedMessage()
    {
        Method = "Network.responseReceived";
    }
}

public class ResponseReceivedParams
{
    [JsonPropertyName("requestId")]
    public string? RequestId { get; set; }

    [JsonPropertyName("loaderId")]
    public string? LoaderId { get; set; }

    [JsonPropertyName("timestamp")]
    public double? Timestamp { get; set; }

    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("response")]
    public Response? Response { get; set; }

    [JsonPropertyName("hasExtraInfo")]
    public bool? HasExtraInfo { get; set; }

    [JsonPropertyName("frameId")]
    public string? FrameId { get; set; }
}

public class Response
{
    [JsonPropertyName("url")]
    public string? Url { get; set; }

    [JsonPropertyName("status")]
    public int? Status { get; set; }

    [JsonPropertyName("statusText")]
    public string? StatusText { get; set; }

    [JsonPropertyName("headers")]
    public Dictionary<string, string>? Headers { get; set; }

    [JsonPropertyName("mimeType")]
    public string? MimeType { get; set; }

    [JsonPropertyName("connectionReused")]
    public bool? ConnectionReused { get; set; }

    [JsonPropertyName("connectionId")]
    public int? ConnectionId { get; set; }

    [JsonPropertyName("fromDiskCache")]
    public bool? FromDiskCache { get; set; }

    [JsonPropertyName("fromServiceWorker")]
    public bool? FromServiceWorker { get; set; }

    [JsonPropertyName("fromPrefetchCache")]
    public bool? FromPrefetchCache { get; set; }

    [JsonPropertyName("encodedDataLength")]
    public int? EncodedDataLength { get; set; }

    [JsonPropertyName("protocol")]
    public string? Protocol { get; set; }

    [JsonPropertyName("alternateProtocolUsage")]
    public string? AlternateProtocolUsage { get; set; }

    [JsonPropertyName("securityState")]
    public string? SecurityState { get; set; }
}

public class Request
{
    [JsonPropertyName("url")]
    public string? Url { get; set; }
    [JsonPropertyName("method")]
    public string? Method { get; set; }
    [JsonPropertyName("headers")]
    public Dictionary<string, string>? Headers { get; set; }
    [JsonPropertyName("postData")]
    public string? PostData { get; set; }
}

public class Initiator
{
    [JsonPropertyName("type")]
    public string? Type { get; set; }
}

public class LoadingFinishedMessage : Message<LoadingFinishedParams>
{
    public LoadingFinishedMessage()
    {
        Method = "Network.loadingFinished";
    }
}

public class LoadingFinishedParams
{
    [JsonPropertyName("requestId")]
    public string? RequestId { get; set; }

    [JsonPropertyName("timestamp")]
    public double? Timestamp { get; set; }

    [JsonPropertyName("encodedDataLength")]
    public long? EncodedDataLength { get; set; }
}

public class LoadingFailedMessage : Message<LoadingFailedParams>
{
    public LoadingFailedMessage()
    {
        Method = "Network.loadingFailed";
    }
}

public class LoadingFailedParams
{
    [JsonPropertyName("requestId")]
    public string? RequestId { get; set; }

    [JsonPropertyName("timestamp")]
    public double? Timestamp { get; set; }

    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("errorText")]
    public string? ErrorText { get; set; }

    [JsonPropertyName("canceled")]
    public bool? Canceled { get; set; }

    [JsonPropertyName("blockedReason")]
    public string? BlockedReason { get; set; }
}

public class EntryAddedMessage : Message<EntryAddedParams>
{
    public EntryAddedMessage()
    {
        Method = "Log.entryAdded";
    }
}

public class EntryAddedParams
{
    [JsonPropertyName("entry")]
    public Entry? Entry { get; set; }
}

public class Entry
{
    [JsonPropertyName("source")]
    public string? Source { get; set; }
    [JsonPropertyName("level")]
    public string? Level { get; set; }
    [JsonPropertyName("text")]
    public string? Text { get; set; }
    [JsonPropertyName("timestamp")]
    public double? Timestamp { get; set; }
    [JsonPropertyName("url")]
    public string? Url { get; set; }
    [JsonPropertyName("networkRequestId")]
    public string? NetworkRequestId { get; set; }

    public static string GetLevel(MessageType messageType)
    {
        return messageType switch
        {
            MessageType.Normal => "info",
            MessageType.InterceptedRequest => "info",
            MessageType.PassedThrough => "info",
            MessageType.Warning => "warning",
            MessageType.Tip => "info",
            MessageType.Failed => "error",
            MessageType.Chaos => "error",
            MessageType.Mocked => "info",
            MessageType.InterceptedResponse => "info",
            _ => "info"
        };
    }
}

public class GetResponseBodyMessage : Message<GetResponseBodyParams>
{
    public GetResponseBodyMessage()
    {
        Method = "Network.getResponseBody";
    }
}

public class GetResponseBodyParams
{
    [JsonPropertyName("requestId")]
    public string? RequestId { get; set; }
}

public class GetResponseBodyResult : MessageResult<GetResponseBodyResultParams>
{
}

public class GetResponseBodyResultParams
{
    [JsonPropertyName("body")]
    public string? Body { get; set; }
    [JsonPropertyName("base64Encoded")]
    public bool? Base64Encoded { get; set; }
}

public abstract class MessageResult<TResult>
{
    [JsonPropertyName("id")]
    public int Id { get; set; }
    [JsonPropertyName("result")]
    public TResult? Result { get; set; }
}

public abstract class Message
{
    [JsonPropertyName("id")]
    public int? Id { get; set; }

    [JsonPropertyName("method")]
    public string? Method { get; set; }
}

public abstract class Message<TParams> : Message
{
    [JsonPropertyName("params")]
    public TParams? Params { get; set; }
}