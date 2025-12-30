// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

namespace DevProxy.Abstractions.Proxy;

/// <summary>
/// Represents the direction of stdio message flow.
/// </summary>
public enum StdioMessageDirection
{
    /// <summary>
    /// Message flowing from parent to child process (stdin).
    /// </summary>
    Stdin,

    /// <summary>
    /// Message flowing from child to parent process (stdout).
    /// </summary>
    Stdout,

    /// <summary>
    /// Error message flowing from child to parent process (stderr).
    /// </summary>
    Stderr
}

/// <summary>
/// Contains information about the stdio session.
/// </summary>
public class StdioSession
{
    /// <summary>
    /// The command being proxied (executable name).
    /// </summary>
    public required string Command { get; init; }

    /// <summary>
    /// The full command line arguments.
    /// </summary>
    public required IReadOnlyList<string> Args { get; init; }

    /// <summary>
    /// The process ID of the child process.
    /// </summary>
    public int? ProcessId { get; set; }
}

/// <summary>
/// Base class for stdio event arguments.
/// </summary>
public class StdioEventArgsBase
{
    /// <summary>
    /// Session-scoped data that plugins can use to share information.
    /// </summary>
    public Dictionary<string, object> SessionData { get; init; } = [];

    /// <summary>
    /// Global data that plugins can use to share information across sessions.
    /// </summary>
    public Dictionary<string, object> GlobalData { get; init; } = [];

    /// <summary>
    /// Information about the stdio session.
    /// </summary>
    public required StdioSession Session { get; init; }
}

/// <summary>
/// Event arguments for stdin messages (before forwarding to child).
/// </summary>
public class StdioRequestArgs : StdioEventArgsBase
{
    /// <summary>
    /// The message content.
    /// </summary>
    public required IReadOnlyList<byte> Body { get; init; }

    /// <summary>
    /// The message content as a string (UTF-8 decoded).
    /// </summary>
    public string BodyString => System.Text.Encoding.UTF8.GetString(Body is byte[] arr ? arr : [.. Body]);

    /// <summary>
    /// State that plugins can use to indicate the message has been handled.
    /// If HasBeenSet is true, the message will not be forwarded to the child.
    /// </summary>
    public required ResponseState ResponseState { get; init; }

    /// <summary>
    /// Mock stdout response to send to the parent process.
    /// Set this when ResponseState.HasBeenSet is true to provide a mock response.
    /// </summary>
    public string? StdoutResponse { get; set; }

    /// <summary>
    /// Mock stderr response to send to the parent process.
    /// Set this when ResponseState.HasBeenSet is true to provide an error response.
    /// </summary>
    public string? StderrResponse { get; set; }

    /// <summary>
    /// Checks if the plugin should execute based on ResponseState.
    /// </summary>
    public bool ShouldExecute() => !ResponseState.HasBeenSet;
}

/// <summary>
/// Event arguments for stdout/stderr messages (after receiving from child).
/// </summary>
public class StdioResponseArgs : StdioEventArgsBase
{
    /// <summary>
    /// The message content.
    /// </summary>
    public required IReadOnlyList<byte> Body { get; init; }

    /// <summary>
    /// The message content as a string (UTF-8 decoded).
    /// </summary>
    public string BodyString => System.Text.Encoding.UTF8.GetString(Body is byte[] arr ? arr : [.. Body]);

    /// <summary>
    /// The direction of the message (Stdout or Stderr).
    /// </summary>
    public required StdioMessageDirection Direction { get; init; }

    /// <summary>
    /// State that plugins can use to indicate the message has been handled.
    /// If HasBeenSet is true, the message will not be forwarded to the parent.
    /// </summary>
    public required ResponseState ResponseState { get; init; }
}

/// <summary>
/// Represents a log entry for a stdio request/response pair.
/// </summary>
public class StdioRequestLog
{
    /// <summary>
    /// The command being proxied.
    /// </summary>
    public required string Command { get; init; }

    /// <summary>
    /// The stdin message (request).
    /// </summary>
    public IReadOnlyList<byte>? StdinBody { get; set; }

    /// <summary>
    /// The stdin message as a string.
    /// </summary>
    public string? StdinBodyString => StdinBody is null ? null : System.Text.Encoding.UTF8.GetString(StdinBody is byte[] arr ? arr : [.. StdinBody]);

    /// <summary>
    /// The stdout message (response).
    /// </summary>
    public IReadOnlyList<byte>? StdoutBody { get; set; }

    /// <summary>
    /// The stdout message as a string.
    /// </summary>
    public string? StdoutBodyString => StdoutBody is null ? null : System.Text.Encoding.UTF8.GetString(StdoutBody is byte[] arr ? arr : [.. StdoutBody]);

    /// <summary>
    /// The stderr message (if any).
    /// </summary>
    public IReadOnlyList<byte>? StderrBody { get; set; }

    /// <summary>
    /// The stderr message as a string.
    /// </summary>
    public string? StderrBodyString => StderrBody is null ? null : System.Text.Encoding.UTF8.GetString(StderrBody is byte[] arr ? arr : [.. StderrBody]);

    /// <summary>
    /// Timestamp when the stdin was received.
    /// </summary>
    public DateTimeOffset? StdinTimestamp { get; set; }

    /// <summary>
    /// Timestamp when the stdout/stderr was received.
    /// </summary>
    public DateTimeOffset? ResponseTimestamp { get; set; }

    /// <summary>
    /// The message for logging purposes.
    /// </summary>
    public string? Message { get; set; }

    /// <summary>
    /// The message type for logging purposes.
    /// </summary>
    public MessageType MessageType { get; set; }

    /// <summary>
    /// The name of the plugin that handled/logged this request.
    /// </summary>
    public string? PluginName { get; set; }
}

/// <summary>
/// Event arguments for stdio request log events.
/// </summary>
public class StdioRequestLogArgs(StdioRequestLog requestLog)
{
    /// <summary>
    /// The request log entry.
    /// </summary>
    public StdioRequestLog RequestLog { get; set; } = requestLog ??
        throw new ArgumentNullException(nameof(requestLog));
}

/// <summary>
/// Event arguments for stdio recording stop events.
/// </summary>
public class StdioRecordingArgs(IEnumerable<StdioRequestLog> requestLogs) : StdioEventArgsBase
{
    /// <summary>
    /// All recorded stdio request logs.
    /// </summary>
    public IEnumerable<StdioRequestLog> RequestLogs { get; set; } = requestLogs ??
        throw new ArgumentNullException(nameof(requestLogs));
}
