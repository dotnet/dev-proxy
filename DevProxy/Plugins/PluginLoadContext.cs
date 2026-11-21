// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.Loader;

namespace DevProxy.Plugins;

sealed class PluginLoadContext : AssemblyLoadContext
{
    private readonly AssemblyDependencyResolver _resolver;
    // Files this context loaded from, with their state at load time, so a
    // reload can tell whether the plugin or any of its dependencies changed.
    private readonly ConcurrentDictionary<string, FileStamp> _files = new(StringComparer.Ordinal);

    public PluginLoadContext(string pluginPath)
    {
        TrackFile(pluginPath);
        TrackFile(Path.ChangeExtension(pluginPath, ".deps.json"));
        _resolver = new(pluginPath);
    }

    /// <summary>
    /// Returns true when none of the files this context loaded from have changed.
    /// </summary>
    public bool IsUpToDate() => _files.All(f => FileStamp.Get(f.Key) == f.Value);

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        var assemblyPath = _resolver.ResolveAssemblyToPath(assemblyName);
        if (assemblyPath is null)
        {
            return null;
        }

        TrackFile(assemblyPath);
        return LoadFromAssemblyPath(assemblyPath);
    }

    protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
    {
        var libraryPath = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
        if (libraryPath is null)
        {
            return IntPtr.Zero;
        }

        TrackFile(libraryPath);
        return LoadUnmanagedDllFromPath(libraryPath);
    }

    private void TrackFile(string path) => _files.TryAdd(Path.GetFullPath(path), FileStamp.Get(path));

    private readonly record struct FileStamp(DateTime LastWriteTimeUtc, long Length)
    {
        public static FileStamp Get(string path)
        {
            var fileInfo = new FileInfo(path);
            return fileInfo.Exists ? new(fileInfo.LastWriteTimeUtc, fileInfo.Length) : new(DateTime.MinValue, -1);
        }
    }
}
