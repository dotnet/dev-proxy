// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Diagnostics;
using System.Text;
using DevProxy.Abstractions.Plugins;
using DevProxy.Abstractions.Proxy;

namespace DevProxy.Stdio;

/// <summary>
/// Manages a child process and forwards stdin/stdout/stderr streams between
/// the parent process and the child process, allowing for interception and
/// modification of the data via plugins.
/// </summary>
/// <remarks>
/// Creates a new ProxySession instance.
/// </remarks>
/// <param name="args">The command and arguments to execute.</param>
/// <param name="plugins">The plugins to execute for stdin/stdout/stderr interception.</param>
/// <param name="globalData">Global data shared across sessions.</param>
/// <param name="logger">Optional logger for diagnostic messages.</param>
internal sealed class ProxySession(
    string[] args,
    IEnumerable<IStdioPlugin> plugins,
    Dictionary<string, object> globalData,
    ILogger? logger = null) : IDisposable
{
    private readonly string[] _args = args;
    private readonly CancellationTokenSource _cts = new();
    private readonly ILogger? _logger = logger;
    private readonly IEnumerable<IStdioPlugin> _plugins = plugins;
    private readonly Dictionary<string, object> _sessionData = [];
    private readonly Dictionary<string, object> _globalData = globalData;
    private readonly List<StdioRequestLog> _requestLogs = [];

    private Process? _process;
    private Stream? _parentStdout;
    private Stream? _childStdin;
    private StdioSession? _stdioSession;

    private readonly SemaphoreSlim _writeSemaphore = new(1, 1);
    private bool _disposed;

    /// <summary>
    /// Write data directly to the child process's stdin.
    /// </summary>
    /// <param name="message">The message to write.</param>
    public async Task WriteToChildStdinAsync(string message)
    {
        if (_childStdin == null)
        {
            return;
        }

        var bytes = Encoding.UTF8.GetBytes(message);
        await _writeSemaphore.WaitAsync();
        try
        {
            Log("INJECT >>>", bytes, bytes.Length);
            await _childStdin.WriteAsync(bytes);
            await _childStdin.FlushAsync();
        }
        catch (IOException ex)
        {
            _logger?.LogDebug(ex, "Error writing to child stdin");
        }
        finally
        {
            _ = _writeSemaphore.Release();
        }
    }

    /// <summary>
    /// Write data directly to the parent's stdout (as if it came from child).
    /// </summary>
    /// <param name="message">The message to write.</param>
    public async Task WriteToParentStdoutAsync(string message)
    {
        if (_parentStdout == null)
        {
            return;
        }

        var bytes = Encoding.UTF8.GetBytes(message);
        await _writeSemaphore.WaitAsync();
        try
        {
            Log("INJECT <<< STDOUT", bytes, bytes.Length);
            await _parentStdout.WriteAsync(bytes);
            await _parentStdout.FlushAsync();
        }
        catch (IOException ex)
        {
            _logger?.LogDebug(ex, "Error writing to parent stdout");
        }
        finally
        {
            _ = _writeSemaphore.Release();
        }
    }

    /// <summary>
    /// Runs the proxy session, forwarding stdin/stdout/stderr between the parent and child processes.
    /// </summary>
    /// <returns>The exit code of the child process.</returns>
    public async Task<int> RunAsync()
    {
        _logger?.LogDebug("Starting proxy session for: {Command}", string.Join(" ", _args));

        _stdioSession = new StdioSession
        {
            Command = _args[0],
            Args = [.. _args.Skip(1)]
        };

        var psi = new ProcessStartInfo
        {
            FileName = _args[0],
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        for (var i = 1; i < _args.Length; i++)
        {
            psi.ArgumentList.Add(_args[i]);
        }

        _process = new Process { StartInfo = psi };

        try
        {
            _ = _process.Start();
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to start process: {FileName}", psi.FileName);
            throw;
        }

        _parentStdout = Console.OpenStandardOutput();
        _childStdin = _process.StandardInput.BaseStream;
        _stdioSession.ProcessId = _process.Id;

        _logger?.LogDebug("Process started with PID: {ProcessId}", _process.Id);

        // Check if process exited immediately (common for command not found, etc.)
        if (_process.HasExited)
        {
            var stderrContent = await _process.StandardError.ReadToEndAsync();
            if (!string.IsNullOrEmpty(stderrContent))
            {
                _logger?.LogError("Process exited immediately with stderr: {Stderr}", stderrContent);
            }
            _logger?.LogError("Process exited immediately with code: {ExitCode}", _process.ExitCode);
            return _process.ExitCode;
        }

        // Start forwarding tasks
        var stdinTask = ForwardStdinAsync();
        var stdoutTask = ForwardStdoutAsync();
        var stderrTask = ForwardStderrAsync();

        // Wait for process to exit
        await _process.WaitForExitAsync();

        _logger?.LogDebug("Process exited with code: {ExitCode}", _process.ExitCode);

        // Give streams a moment to flush, then cancel
        await Task.Delay(100);
        await _cts.CancelAsync();

        try
        {
            await Task.WhenAll(stdinTask, stdoutTask, stderrTask);
        }
        catch (OperationCanceledException)
        {
            // Expected when cancellation is requested
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Error during stream forwarding cleanup");
        }

        // Notify plugins that recording has stopped
        await NotifyRecordingStopAsync();

        return _process.ExitCode;
    }

    private async Task ForwardStdinAsync()
    {
        var buffer = new byte[4096];
        using var stdin = Console.OpenStandardInput();

        try
        {
            while (!_cts.Token.IsCancellationRequested)
            {
                // Console stdin doesn't support cancellation well, so we use a blocking read
                // on a thread pool thread and check for cancellation/process exit periodically
                var readTask = Task.Run(() => stdin.Read(buffer, 0, buffer.Length), CancellationToken.None);

                while (!readTask.IsCompleted)
                {
                    // Check every 100ms if we should stop
                    var delayTask = Task.Delay(100, _cts.Token);
                    var completedTask = await Task.WhenAny(readTask, delayTask);

                    if (completedTask == delayTask && _cts.Token.IsCancellationRequested)
                    {
                        // Cancellation requested - exit
                        return;
                    }

                    if (_process?.HasExited == true)
                    {
                        // Process exited - close stdin to unblock the read
                        return;
                    }
                }

                var bytesRead = await readTask;
                if (bytesRead == 0)
                {
                    break;
                }

                var data = buffer[..bytesRead];
                Log("STDIN >>>", buffer, bytesRead);

                // Log intercepted stdin (similar to HTTP InterceptedRequest)
                await LogInterceptedStdioAsync(data, StdioMessageDirection.Stdin);

                // Execute plugins and check if message was consumed
                var responseState = new ResponseState();
                var requestArgs = new StdioRequestArgs
                {
                    Body = data,
                    ResponseState = responseState,
                    Session = _stdioSession!,
                    SessionData = _sessionData,
                    GlobalData = _globalData
                };

                await ExecuteBeforeStdinPluginsAsync(requestArgs);

                // Only forward to child if not consumed
                if (!responseState.HasBeenSet)
                {
                    await _writeSemaphore.WaitAsync(_cts.Token);
                    try
                    {
                        await _childStdin!.WriteAsync(buffer.AsMemory(0, bytesRead), _cts.Token);
                        await _childStdin.FlushAsync(_cts.Token);
                    }
                    finally
                    {
                        _ = _writeSemaphore.Release();
                    }
                }
                else
                {
                    // Send mock response if set by a plugin
                    await SendMockResponseAsync(requestArgs);
                }

                // Create request log entry for this stdin message
                var requestLog = new StdioRequestLog
                {
                    Command = _stdioSession!.Command,
                    StdinBody = data,
                    StdinTimestamp = DateTimeOffset.UtcNow,
                    MessageType = responseState.HasBeenSet ? MessageType.Mocked : MessageType.PassedThrough
                };
                _requestLogs.Add(requestLog);

                // Notify plugins of the request log
                await NotifyStdioRequestLogAsync(requestLog);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected
        }
        catch (IOException ex)
        {
            _logger?.LogDebug(ex, "Error forwarding stdin");
        }
        finally
        {
            try
            {
                _process?.StandardInput.Close();
            }
            catch
            {
                // Ignore errors during cleanup
            }
        }
    }

    private async Task ForwardStdoutAsync()
    {
        var buffer = new byte[4096];

        try
        {
            while (!_cts.Token.IsCancellationRequested)
            {
                var bytesRead = await _process!.StandardOutput.BaseStream.ReadAsync(buffer, _cts.Token);
                if (bytesRead == 0)
                {
                    break;
                }

                var data = buffer[..bytesRead];
                Log("<<< STDOUT", buffer, bytesRead);

                // Log intercepted stdout (similar to HTTP InterceptedResponse)
                await LogInterceptedStdioAsync(data, StdioMessageDirection.Stdout);

                // Execute plugins and check if message was consumed
                var responseState = new ResponseState();
                var responseArgs = new StdioResponseArgs
                {
                    Body = data,
                    Direction = StdioMessageDirection.Stdout,
                    ResponseState = responseState,
                    Session = _stdioSession!,
                    SessionData = _sessionData,
                    GlobalData = _globalData
                };

                await ExecuteAfterStdoutPluginsAsync(responseArgs);

                // Only forward to parent if not consumed
                if (!responseState.HasBeenSet)
                {
                    await _writeSemaphore.WaitAsync(_cts.Token);
                    try
                    {
                        await _parentStdout!.WriteAsync(buffer.AsMemory(0, bytesRead), _cts.Token);
                        await _parentStdout.FlushAsync(_cts.Token);
                    }
                    finally
                    {
                        _ = _writeSemaphore.Release();
                    }
                }

                // Update the last request log with stdout response
                UpdateLastRequestLogWithResponse(data, StdioMessageDirection.Stdout);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected
        }
        catch (IOException ex)
        {
            _logger?.LogDebug(ex, "Error forwarding stdout");
        }
    }

    private async Task ForwardStderrAsync()
    {
        var buffer = new byte[4096];
        using var stderr = Console.OpenStandardError();

        try
        {
            while (!_cts.Token.IsCancellationRequested)
            {
                var bytesRead = await _process!.StandardError.BaseStream.ReadAsync(buffer, _cts.Token);
                if (bytesRead == 0)
                {
                    break;
                }

                var data = buffer[..bytesRead];
                Log("<<< STDERR", buffer, bytesRead);

                // Log intercepted stderr (similar to HTTP InterceptedResponse)
                await LogInterceptedStdioAsync(data, StdioMessageDirection.Stderr);

                // Execute plugins and check if message was consumed
                var responseState = new ResponseState();
                var responseArgs = new StdioResponseArgs
                {
                    Body = data,
                    Direction = StdioMessageDirection.Stderr,
                    ResponseState = responseState,
                    Session = _stdioSession!,
                    SessionData = _sessionData,
                    GlobalData = _globalData
                };

                await ExecuteAfterStderrPluginsAsync(responseArgs);

                // Only forward to parent if not consumed
                if (!responseState.HasBeenSet)
                {
                    await stderr.WriteAsync(buffer.AsMemory(0, bytesRead), _cts.Token);
                    await stderr.FlushAsync(_cts.Token);
                }

                // Update the last request log with stderr response
                UpdateLastRequestLogWithResponse(data, StdioMessageDirection.Stderr);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected
        }
        catch (IOException ex)
        {
            _logger?.LogDebug(ex, "Error forwarding stderr");
        }
    }

    private async Task ExecuteBeforeStdinPluginsAsync(StdioRequestArgs args)
    {
        foreach (var plugin in _plugins.Where(p => p.Enabled))
        {
            _cts.Token.ThrowIfCancellationRequested();

            try
            {
                await plugin.BeforeStdinAsync(args, _cts.Token);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error executing plugin {PluginName}.BeforeStdinAsync", plugin.Name);
            }
        }
    }

    private async Task ExecuteAfterStdoutPluginsAsync(StdioResponseArgs args)
    {
        foreach (var plugin in _plugins.Where(p => p.Enabled))
        {
            _cts.Token.ThrowIfCancellationRequested();

            try
            {
                await plugin.AfterStdoutAsync(args, _cts.Token);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error executing plugin {PluginName}.AfterStdoutAsync", plugin.Name);
            }
        }
    }

    private async Task ExecuteAfterStderrPluginsAsync(StdioResponseArgs args)
    {
        foreach (var plugin in _plugins.Where(p => p.Enabled))
        {
            _cts.Token.ThrowIfCancellationRequested();

            try
            {
                await plugin.AfterStderrAsync(args, _cts.Token);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error executing plugin {PluginName}.AfterStderrAsync", plugin.Name);
            }
        }
    }

    /// <summary>
    /// Logs intercepted stdio messages to plugins, similar to how ProxyEngine
    /// logs InterceptedRequest/InterceptedResponse for HTTP.
    /// </summary>
    private async Task LogInterceptedStdioAsync(byte[] data, StdioMessageDirection direction)
    {
        var messageType = direction == StdioMessageDirection.Stdin
            ? MessageType.InterceptedRequest
            : MessageType.InterceptedResponse;

        var requestLog = new StdioRequestLog
        {
            Command = _stdioSession!.Command,
            MessageType = messageType,
            Message = direction switch
            {
                StdioMessageDirection.Stdin => $"STDIN {_stdioSession.Command}",
                StdioMessageDirection.Stdout => $"STDOUT {_stdioSession.Command}",
                StdioMessageDirection.Stderr => $"STDERR {_stdioSession.Command}",
                _ => $"{direction} {_stdioSession.Command}"
            }
        };

        // Set the appropriate body based on direction
        if (direction == StdioMessageDirection.Stdin)
        {
            requestLog.StdinBody = data;
            requestLog.StdinTimestamp = DateTimeOffset.UtcNow;
        }
        else if (direction == StdioMessageDirection.Stdout)
        {
            requestLog.StdoutBody = data;
            requestLog.ResponseTimestamp = DateTimeOffset.UtcNow;
        }
        else if (direction == StdioMessageDirection.Stderr)
        {
            requestLog.StderrBody = data;
            requestLog.ResponseTimestamp = DateTimeOffset.UtcNow;
        }

        var args = new StdioRequestLogArgs(requestLog);

        foreach (var plugin in _plugins.Where(p => p.Enabled))
        {
            try
            {
                await plugin.AfterStdioRequestLogAsync(args, _cts.Token);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error executing plugin {PluginName}.AfterStdioRequestLogAsync (intercepted)", plugin.Name);
            }
        }
    }

    private async Task NotifyStdioRequestLogAsync(StdioRequestLog requestLog)
    {
        var args = new StdioRequestLogArgs(requestLog);

        foreach (var plugin in _plugins.Where(p => p.Enabled))
        {
            try
            {
                await plugin.AfterStdioRequestLogAsync(args, _cts.Token);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error executing plugin {PluginName}.AfterStdioRequestLogAsync", plugin.Name);
            }
        }
    }

    private async Task NotifyRecordingStopAsync()
    {
        var args = new StdioRecordingArgs(_requestLogs)
        {
            Session = _stdioSession!,
            SessionData = _sessionData,
            GlobalData = _globalData
        };

        foreach (var plugin in _plugins.Where(p => p.Enabled))
        {
            try
            {
                await plugin.AfterStdioRecordingStopAsync(args, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error executing plugin {PluginName}.AfterStdioRecordingStopAsync", plugin.Name);
            }
        }
    }

    private void UpdateLastRequestLogWithResponse(byte[] data, StdioMessageDirection direction)
    {
        if (_requestLogs.Count == 0)
        {
            return;
        }

        var lastLog = _requestLogs[^1];
        lastLog.ResponseTimestamp = DateTimeOffset.UtcNow;

        if (direction == StdioMessageDirection.Stdout)
        {
            lastLog.StdoutBody = data;
        }
        else if (direction == StdioMessageDirection.Stderr)
        {
            lastLog.StderrBody = data;
        }
    }

    /// <summary>
    /// Sends mock responses that were set by plugins on the request args.
    /// </summary>
    private async Task SendMockResponseAsync(StdioRequestArgs requestArgs)
    {
        // Send mock stdout if set by a plugin
        if (!string.IsNullOrEmpty(requestArgs.StdoutResponse))
        {
            await WriteToParentStdoutAsync(requestArgs.StdoutResponse);

            // Update request log with mock response
            var stdoutBytes = Encoding.UTF8.GetBytes(requestArgs.StdoutResponse);
            UpdateLastRequestLogWithResponse(stdoutBytes, StdioMessageDirection.Stdout);
        }

        // Send mock stderr if set by a plugin
        if (!string.IsNullOrEmpty(requestArgs.StderrResponse))
        {
            var stderrBytes = Encoding.UTF8.GetBytes(requestArgs.StderrResponse);
            using var stderrStream = Console.OpenStandardError();
            await stderrStream.WriteAsync(stderrBytes);
            await stderrStream.FlushAsync();

            // Update request log
            UpdateLastRequestLogWithResponse(stderrBytes, StdioMessageDirection.Stderr);
        }
    }

    private void Log(string direction, byte[] data, int count)
    {
        if (_logger == null)
        {
            return;
        }

        var text = Encoding.UTF8.GetString(data, 0, count);
        _logger.LogInformation("{Direction} ({Count} bytes): {Text}", direction, count, text);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _parentStdout?.Dispose();
        _process?.Dispose();
        _cts.Dispose();
        _writeSemaphore.Dispose();
    }
}
