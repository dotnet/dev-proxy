// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections.Concurrent;

namespace DevProxy.Abstractions.Plugins;

/// <summary>
/// Remembers, per process, which files passed schema validation so that
/// unchanged files aren't validated again on every load or hot reload.
/// Only passes are stored. A file is considered unchanged when its size,
/// last write time and (optionally) a hash of the validated contents match.
/// </summary>
internal static class SchemaValidationCache
{
    internal readonly record struct Key(string FilePath, string SchemaUrl, string? Section);
    internal readonly record struct FileVersion(long Length, long LastWriteTimeUtcTicks, string? ContentHash);

    // one entry per file, schema and section, so the cache doesn't grow when files change
    private static readonly ConcurrentDictionary<Key, FileVersion> _passed = new();

    public static Key CreateKey(string filePath, string schemaUrl, string? section = null) =>
        new(Path.GetFullPath(filePath), schemaUrl, section);

    public static FileVersion? GetFileVersion(string filePath, string? contentHash = null)
    {
        var fileInfo = new FileInfo(filePath);
        return fileInfo.Exists
            ? new(fileInfo.Length, fileInfo.LastWriteTimeUtc.Ticks, contentHash)
            : null;
    }

    /// <summary>
    /// Returns true if the contents read after <paramref name="versionBefore"/> was
    /// taken already passed validation. Call after reading the contents: the file
    /// must still match <paramref name="versionBefore"/>, so that an edit made while
    /// it was being read isn't mistaken for the version that passed.
    /// </summary>
    public static bool HasPassed(Key key, FileVersion? versionBefore) =>
        versionBefore is { } before &&
        _passed.TryGetValue(key, out var passedVersion) &&
        passedVersion == before &&
        GetFileVersion(key.FilePath, before.ContentHash) == before;

    /// <summary>
    /// Records the validation result. A pass is stored only if the file didn't
    /// change while it was being validated (<paramref name="versionBefore"/> is
    /// read before reading the file and compared with the current version).
    /// </summary>
    public static void SetResult(Key key, FileVersion? versionBefore, bool isValid)
    {
        if (isValid &&
            versionBefore is { } before &&
            GetFileVersion(key.FilePath, before.ContentHash) == before)
        {
            _passed[key] = before;
        }
        else
        {
            _ = _passed.TryRemove(key, out _);
        }
    }
}
