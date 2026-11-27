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
using Microsoft.Extensions.Logging;

public class MyPlugin(
    ILogger logger,
    ISet<UrlToWatch> urlsToWatch) : BasePlugin(logger, urlsToWatch)
{
    public override string Name => nameof(MyPlugin);

    public override async Task BeforeRequestAsync(
        ProxyRequestArgs e,
        CancellationToken cancellationToken)
    {
        // Your custom logic here
    }
}
```

## Target framework

This package targets `net10.0` and `net11.0`. Target the same .NET version as the Dev Proxy release that runs your plugin, or an earlier one. Dev Proxy can load a plugin built for an earlier .NET version, but not for a later one.

## Don't ship shared assemblies with your plugin

Dev Proxy already loads `DevProxy.Abstractions` and the `Microsoft.Extensions.*` packages that it depends on. If your plugin ships its own copy of these assemblies, Dev Proxy loads a second copy, and your plugin's types won't match Dev Proxy's types. To avoid this, reference `DevProxy.Abstractions` and the shared packages with `Private=false` and `ExcludeAssets=runtime`:

```xml
<ItemGroup>
  <PackageReference Include="DevProxy.Abstractions" Version="x.y.z">
    <Private>false</Private>
    <ExcludeAssets>runtime</ExcludeAssets>
  </PackageReference>
  <PackageReference Include="Microsoft.Extensions.Configuration" Version="x.y.z">
    <Private>false</Private>
    <ExcludeAssets>runtime</ExcludeAssets>
  </PackageReference>
</ItemGroup>
```

For more information, see the [Dev Proxy documentation](https://aka.ms/devproxy/docs).
