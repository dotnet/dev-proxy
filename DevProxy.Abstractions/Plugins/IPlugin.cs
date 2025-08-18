// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Abstractions.Proxy;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System.CommandLine;
using Unobtanium.Web.Proxy.Events;

namespace DevProxy.Abstractions.Plugins;

public interface IPlugin
{
    string Name { get; }
    bool Enabled { get; }
    Option[] GetOptions();
    Command[] GetCommands();

    Task InitializeAsync(InitArgs e, CancellationToken cancellationToken);
    void OptionsLoaded(OptionsLoadedArgs e);

    Func<Models.RequestArguments, CancellationToken, Task<Models.PluginResponse>>? OnRequestAsync { get; set; }
    Func<ResponseEventArguments, CancellationToken, Task<ResponseEventResponse?>>? OnResponseAsync { get; set; }
    Task BeforeRequestAsync(ProxyRequestArgs e, CancellationToken cancellationToken);
    Task BeforeResponseAsync(ProxyResponseArgs e, CancellationToken cancellationToken);
    Task AfterResponseAsync(ProxyResponseArgs e, CancellationToken cancellationToken);
    Task AfterRequestLogAsync(RequestLogArgs e, CancellationToken cancellationToken);
    Task AfterRecordingStopAsync(RecordingArgs e, CancellationToken cancellationToken);
    Task MockRequestAsync(EventArgs e, CancellationToken cancellationToken);
}

public interface IPlugin<TConfiguration> : IPlugin
{
    TConfiguration Configuration { get; }
    IConfigurationSection ConfigurationSection { get; }

    void Register(IServiceCollection services, TConfiguration configuration);
}