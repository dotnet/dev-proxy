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

    Task InitializeAsync(InitArgs e);
    void OptionsLoaded(OptionsLoadedArgs e);
    Task BeforeRequestAsync(ProxyRequestArgs e);
    Task BeforeResponseAsync(ProxyResponseArgs e);
    Task AfterResponseAsync(ProxyResponseArgs e);
    Task AfterRequestLogAsync(RequestLogArgs e);
    Task AfterRecordingStopAsync(RecordingArgs e);
    Task MockRequestAsync(EventArgs e);
}

public interface IPlugin<TConfiguration> : IPlugin
{
    TConfiguration Configuration { get; }
    IConfigurationSection ConfigurationSection { get; }

    void Register(IServiceCollection services, TConfiguration configuration);
}