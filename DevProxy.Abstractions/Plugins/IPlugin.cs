// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Abstractions.Proxy;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System.CommandLine;

namespace DevProxy.Abstractions.Plugins;

public interface IPlugin
{
    string Name { get; }
    bool Enabled { get; }
    Option[] GetOptions();
    Command[] GetCommands();

    Task InitializeAsync(InitArgs e, CancellationToken cancellationToken);
    void OptionsLoaded(OptionsLoadedArgs e);


    /// <summary>
    /// Implement this to handle requests.
    /// </summary>
    /// <remarks>This is <see langword="null"/> by default, so we can filter plugins based on implementation.</remarks>
    Func<RequestArguments, CancellationToken, Task<PluginResponse>>? OnRequestAsync { get; }

    /// <summary>
    /// Implement this to log requests, you cannot modify the request or response here.
    /// </summary>
    /// <remarks>This is <see langword="null"/> by default, so we can filter plugins based on implementation.</remarks>
    Func<RequestArguments, CancellationToken, Task>? OnRequestLogAsync { get; }

    /// <summary>
    /// Implement this to modify responses from the remote server.
    /// </summary>
    /// <remarks>This is <see langword="null"/> by default, so we can filter plugins based on implementation.</remarks>
    Func<ResponseArguments, CancellationToken, Task<PluginResponse?>>? OnResponseAsync { get; }

    /// <summary>
    /// Implement this to log responses from the remote server.
    /// </summary>
    /// <remarks>Think caching after the fact, combined with <see cref="OnRequestAsync"/>. This is <see langword="null"/> by default, so we can filter plugins based on implementation.</remarks>
    Func<ResponseArguments, CancellationToken, Task>? OnResponseLogAsync { get; }

    Task BeforeRequestAsync(ProxyRequestArgs e, CancellationToken cancellationToken);
    Task BeforeResponseAsync(ProxyResponseArgs e, CancellationToken cancellationToken);
    Task AfterResponseAsync(ProxyResponseArgs e, CancellationToken cancellationToken);

    /// <summary>
    /// Receiving RequestLog messages for each <see cref="Microsoft.Extensions.Logging.ILoggerExtensions.LogRequest(Microsoft.Extensions.Logging.ILogger, string, MessageType, HttpRequestMessage)"/> call.
    /// </summary>
    /// <remarks>This is for collecting log messages not requests itself</remarks>
    /// <param name="e"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    Task AfterRequestLogAsync(RequestLogArgs e, CancellationToken cancellationToken);

    /// <summary>
    /// Executes post-processing tasks after a recording has stopped.
    /// </summary>
    /// <param name="e">The arguments containing details about the recording that has stopped.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    Task AfterRecordingStopAsync(RecordingArgs e, CancellationToken cancellationToken);
    Task MockRequestAsync(EventArgs e, CancellationToken cancellationToken);
}

public interface IPlugin<TConfiguration> : IPlugin
{
    TConfiguration Configuration { get; }
    IConfigurationSection ConfigurationSection { get; }

    void Register(IServiceCollection services, TConfiguration configuration);
}