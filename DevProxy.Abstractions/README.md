# Dev Proxy Abstractions

[![NuGet Version](https://img.shields.io/nuget/v/DevProxy.Abstractions)](https://www.nuget.org/packages/DevProxy.Abstractions)
[![NuGet Downloads](https://img.shields.io/nuget/dt/DevProxy.Abstractions)](https://www.nuget.org/packages/DevProxy.Abstractions)

Abstractions for building custom [Dev Proxy](https://aka.ms/devproxy) plugins. Dev Proxy is an API simulator that helps you effortlessly test your app beyond the happy path.

## What This Package Does

This package provides the interfaces, base classes, and models needed to build custom Dev Proxy plugins. Use it when you want to extend Dev Proxy with your own functionality.

## Usage

Create a new class library project and add a reference to this package:

```bash
dotnet add package DevProxy.Abstractions
```

Then, implement the `IPlugin` interface or inherit from `BasePlugin`:

```csharp
using DevProxy.Abstractions.Plugins;
using DevProxy.Abstractions.Proxy;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

public class MyPlugin : BasePlugin
{
    public override string Name => nameof(MyPlugin);

    public MyPlugin(
        IPluginEvents pluginEvents,
        IProxyContext context,
        ISet<UrlToWatch> urlsToWatch,
        IConfigurationSection? configSection = null
    ) : base(pluginEvents, context, urlsToWatch, configSection)
    {
    }

    public override async Task BeforeRequestAsync(
        ProxyRequestArgs e,
        CancellationToken cancellationToken)
    {
        // Your custom logic here
    }
}
```

For more information, see the [Dev Proxy documentation](https://aka.ms/devproxy/docs).
