// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Diagnostics;
using System.Text;

namespace DevProxy.Stdio;

/// <summary>
/// Manages a child process and forwards stdin/stdout/stderr streams between
/// the parent process and the child process, allowing for interception and
/// modification of the data.
/// </summary>
internal sealed class ProxySession : IDisposable
{
    private readonly string[] _args;
    private readonly CancellationTokenSource _cts = new();
    private readonly ILogger? _logger;

    private Process? _process;
    private Stream? _parentStdout;
    private Stream? _childStdin;

    private readonly SemaphoreSlim _writeSemaphore = new(1, 1);
    private bool _disposed;

    /// <summary>
    /// Handler for inspecting stdin traffic.
    /// Return true to consume the message (don't forward to child), false to pass through.
    /// </summary>
    public Func<string, bool>? OnStdinReceived { get; set; }

    /// <summary>
    /// Handler for inspecting stdout traffic.
    /// Return true to consume the message (don't forward to parent), false to pass through.
    /// </summary>
    public Func<string, bool>? OnStdoutReceived { get; set; }

    /// <summary>
    /// Handler for inspecting stderr traffic.
    /// Return true to consume the message (don't forward to parent), false to pass through.
    /// </summary>
    public Func<string, bool>? OnStderrReceived { get; set; }

    /// <summary>
    /// Creates a new ProxySession instance.
    /// </summary>
    /// <param name="args">The command and arguments to execute.</param>
    /// <param name="logger">Optional logger for diagnostic messages.</param>
    public ProxySession(string[] args, ILogger? logger = null)
    {
        _args = args;
        _logger = logger;
    }

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

                var message = Encoding.UTF8.GetString(buffer, 0, bytesRead);
                Log("STDIN >>>", buffer, bytesRead);

                // Fire handler - if it returns true, the message was consumed
                var consumed = OnStdinReceived?.Invoke(message) ?? false;

                // Only forward to child if not consumed
                if (!consumed)
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

                var message = Encoding.UTF8.GetString(buffer, 0, bytesRead);
                Log("<<< STDOUT", buffer, bytesRead);

                // Fire handler - if it returns true, the message was consumed
                var consumed = OnStdoutReceived?.Invoke(message) ?? false;

                // Only forward to parent if not consumed
                if (!consumed)
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

                var message = Encoding.UTF8.GetString(buffer, 0, bytesRead);
                Log("<<< STDERR", buffer, bytesRead);

                // Fire handler - if it returns true, the message was consumed
                var consumed = OnStderrReceived?.Invoke(message) ?? false;

                // Only forward to parent if not consumed
                if (!consumed)
                {
                    await stderr.WriteAsync(buffer.AsMemory(0, bytesRead), _cts.Token);
                    await stderr.FlushAsync(_cts.Token);
                }
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
