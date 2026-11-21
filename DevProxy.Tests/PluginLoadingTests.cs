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
        public void GetPluginLoadContext_UnchangedFile_ReusesContext()
        {
            var pluginPath = CopyTestAssembly();
            try
            {
                var first = PluginServiceExtensions.GetPluginLoadContext(pluginPath);
                var second = PluginServiceExtensions.GetPluginLoadContext(pluginPath);

                Assert.Same(first, second);
            }
            finally
            {
                File.Delete(pluginPath);
            }
        }

        [Fact]
        public void GetPluginLoadContext_ChangedFile_CreatesNewContext()
        {
            var pluginPath = CopyTestAssembly();
            try
            {
                var first = PluginServiceExtensions.GetPluginLoadContext(pluginPath);
                File.SetLastWriteTimeUtc(pluginPath, File.GetLastWriteTimeUtc(pluginPath).AddMinutes(1));
                var second = PluginServiceExtensions.GetPluginLoadContext(pluginPath);

                Assert.NotSame(first, second);
            }
            finally
            {
                File.Delete(pluginPath);
            }
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

        private static string CopyTestAssembly()
        {
            var pluginPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.dll");
            File.Copy(typeof(PluginLoadingTests).Assembly.Location, pluginPath);
            return pluginPath;
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
