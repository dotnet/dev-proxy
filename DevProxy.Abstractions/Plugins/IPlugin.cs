// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Abstractions.Proxy;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System.CommandLine;

namespace DevProxy.Abstractions.Plugins;

/// <summary>
/// The interface that all plugins must implement.
/// </summary>
/// <remarks>We made it easy for you with the <see cref="BasePlugin"/></remarks>
public interface IPlugin
{
    /// <summary>
    /// Name of the plugin.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Whether the plugin is enabled or not.
    /// </summary>
    bool Enabled { get; }
    Option[] GetOptions();
    Command[] GetCommands();

    /// <summary>
    /// Called once after the plugin is constructed, but before any requests are handled.
    /// </summary>
    /// <param name="e"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    Task InitializeAsync(InitArgs e, CancellationToken cancellationToken);

    /// <summary>
    /// Handles the event triggered when options are successfully loaded.
    /// </summary>
    /// <param name="e">An <see cref="OptionsLoadedArgs"/> instance containing the event data, including the loaded options.</param>
    void OptionsLoaded(OptionsLoadedArgs e);


    /// <summary>
    /// Implement this to handle requests.
    /// </summary>
    /// <remarks>This is <see langword="null"/> by default, so we can filter plugins based on implementation.</remarks>
    Func<RequestArguments, CancellationToken, Task<PluginResponse>>? OnRequestAsync { get; }

    /// <summary>
    /// Implement this to provide guidance for requests, you cannot modify the request or response here.
    /// </summary>
    /// <remarks>This is <see langword="null"/> by default, so we can filter plugins based on implementation.</remarks>
    Func<RequestArguments, CancellationToken, Task>? ProvideRequestGuidanceAsync { get; }

    /// <summary>
    /// Implement this to modify responses from the remote server.
    /// </summary>
    /// <remarks>This is <see langword="null"/> by default, so we can filter plugins based on implementation.</remarks>
    Func<ResponseArguments, CancellationToken, Task<PluginResponse?>>? OnResponseAsync { get; }

    /// <summary>
    /// Implement this to provide guidance based on responses from the remote server.
    /// </summary>
    /// <remarks>Think caching after the fact, combined with <see cref="OnRequestAsync"/>. This is <see langword="null"/> by default, so we can filter plugins based on implementation.</remarks>
    Func<ResponseArguments, CancellationToken, Task>? ProvideResponseGuidanceAsync { get; }

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

    /// <summary>
    /// 
    /// </summary>
    /// <param name="e"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    Task MockRequestAsync(EventArgs e, CancellationToken cancellationToken);
}

public interface IPlugin<TConfiguration> : IPlugin
{
    TConfiguration Configuration { get; }
    IConfigurationSection ConfigurationSection { get; }

    void Register(IServiceCollection services, TConfiguration configuration);
}