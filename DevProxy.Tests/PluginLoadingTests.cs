// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Abstractions.Plugins;
using DevProxy.Abstractions.Proxy;
using Microsoft.Extensions.DependencyInjection;
using System.CommandLine;
using Xunit;

namespace DevProxy.Tests
{
    public class PluginLoadingTests
    {
        [Fact]
        public void GetPluginLoadContext_UnchangedFiles_ReusesContext()
        {
            using var plugin = new TempPlugin();

            var first = PluginServiceExtensions.GetPluginLoadContext(plugin.PluginPath);
            _ = first.LoadFromAssemblyName(new(DependencyName));
            var second = PluginServiceExtensions.GetPluginLoadContext(plugin.PluginPath);

            Assert.Same(first, second);
        }

        [Fact]
        public void GetPluginLoadContext_ChangedPlugin_CreatesNewContext()
        {
            using var plugin = new TempPlugin();

            var first = PluginServiceExtensions.GetPluginLoadContext(plugin.PluginPath);
            TempPlugin.Touch(plugin.PluginPath);
            var second = PluginServiceExtensions.GetPluginLoadContext(plugin.PluginPath);

            Assert.NotSame(first, second);
        }

        [Fact]
        public void GetPluginLoadContext_ChangedLoadedDependency_CreatesNewContext()
        {
            using var plugin = new TempPlugin();

            var first = PluginServiceExtensions.GetPluginLoadContext(plugin.PluginPath);
            var dependency = first.LoadFromAssemblyName(new(DependencyName));
            // loaded from the plugin's folder (the temp path may be a symlink)
            Assert.EndsWith(Path.Combine(Path.GetFileName(Path.GetDirectoryName(plugin.DependencyPath)!), $"{DependencyName}.dll"), dependency.Location, StringComparison.Ordinal);
            TempPlugin.Touch(plugin.DependencyPath);
            var second = PluginServiceExtensions.GetPluginLoadContext(plugin.PluginPath);

            Assert.NotSame(first, second);
        }

        [Fact]
        public void FindPluginType_ReplacedAssemblyFile_FallsBackToLoadedAssembly()
        {
            // Windows locks loaded assemblies, so the file can't be replaced
            if (OperatingSystem.IsWindows())
            {
                return;
            }

            using var plugin = new TempPlugin();
            var context = PluginServiceExtensions.GetPluginLoadContext(plugin.PluginPath);
            var assembly = context.LoadFromAssemblyName(new(Path.GetFileNameWithoutExtension(plugin.PluginPath)));
            // a different assembly on disk has a different MVID and none of the plugin types
            File.Copy(plugin.DependencyPath, plugin.PluginPath, overwrite: true);

            var type = PluginServiceExtensions.FindPluginType(assembly, nameof(LookupPlugin));

            Assert.NotNull(type);
            Assert.Equal(typeof(LookupPlugin).FullName, type.FullName);
        }

        [Fact]
        public void FindPluginType_SkipsSameNamedNonPluginTypes()
        {
            var type = PluginServiceExtensions.FindPluginType(typeof(PluginLoadingTests).Assembly, nameof(LookupPlugin));

            Assert.Equal(typeof(LookupPlugin), type);
        }

        [Fact]
        public void FindPluginType_FindsNestedPluginType()
        {
            var type = PluginServiceExtensions.FindPluginType(typeof(PluginLoadingTests).Assembly, nameof(Outer.NestedLookupPlugin));

            Assert.Equal(typeof(Outer.NestedLookupPlugin), type);
        }

        [Fact]
        public void FindPluginType_UnknownName_ReturnsNull()
        {
            Assert.Null(PluginServiceExtensions.FindPluginType(typeof(PluginLoadingTests).Assembly, "DoesNotExistPlugin"));
        }

        private const string DependencyName = "xunit.assert";

        private sealed class TempPlugin : IDisposable
        {
            private readonly string _directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

            public string PluginPath { get; }
            public string DependencyPath { get; }

            public TempPlugin()
            {
                _ = Directory.CreateDirectory(_directory);
                var source = typeof(PluginLoadingTests).Assembly.Location;
                var sourceDirectory = Path.GetDirectoryName(source)!;
                PluginPath = Path.Combine(_directory, Path.GetFileName(source));
                DependencyPath = Path.Combine(_directory, $"{DependencyName}.dll");
                File.Copy(source, PluginPath);
                File.Copy(Path.ChangeExtension(source, ".deps.json"), Path.ChangeExtension(PluginPath, ".deps.json"));
                File.Copy(Path.Combine(sourceDirectory, $"{DependencyName}.dll"), DependencyPath);
            }

            public static void Touch(string path) =>
                File.SetLastWriteTimeUtc(path, File.GetLastWriteTimeUtc(path).AddMinutes(1));

            public void Dispose()
            {
                try
                {
                    Directory.Delete(_directory, recursive: true);
                }
                catch (IOException)
                {
                    // loaded assemblies may still be locked on Windows
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        }
    }

    public abstract class LookupPluginBase : IPlugin
    {
        public string Name => GetType().Name;
        public bool Enabled => true;
        public Option[] GetOptions() => [];
        public Command[] GetCommands() => [];
        public Task InitializeAsync(InitArgs e, CancellationToken cancellationToken) => Task.CompletedTask;
        public void OptionsLoaded(OptionsLoadedArgs e) { }
        public Task BeforeRequestAsync(ProxyRequestArgs e, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task BeforeResponseAsync(ProxyResponseArgs e, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task AfterResponseAsync(ProxyResponseArgs e, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task AfterRequestLogAsync(RequestLogArgs e, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task AfterRecordingStopAsync(RecordingArgs e, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task MockRequestAsync(EventArgs e, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    public sealed class LookupPlugin : LookupPluginBase;

    public static class Outer
    {
        public sealed class NestedLookupPlugin : LookupPluginBase;
    }
}

// Same name as a plugin type, but not a plugin, so lookup must skip it.
namespace DevProxy.Tests.Aaa
{
    public sealed class LookupPlugin;
}
