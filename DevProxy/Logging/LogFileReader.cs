// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

namespace DevProxy.Logging;

internal sealed class LogFileReader : IDisposable
{
    private readonly StreamReader _reader;
    private readonly LogSinceFilter _filter;
    private string _partialLine = string.Empty;

    public LogFileReader(string path, DateTimeOffset logStartTime, DateTimeOffset? sinceTime)
    {
        _reader = new StreamReader(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite));
        _filter = new LogSinceFilter(logStartTime, sinceTime);
    }

    public async Task<IReadOnlyList<string>> ReadLinesAsync(CancellationToken cancellationToken)
    {
        var content = _partialLine + await _reader.ReadToEndAsync(cancellationToken);
        var lines = content.Split('\n');
        _partialLine = lines[^1];

        List<string> result = [];
        for (var i = 0; i < lines.Length - 1; i++)
        {
            result.AddRange(_filter.AppendLine(lines[i].TrimEnd('\r')));
        }

        return result;
    }

    public void Dispose() => _reader.Dispose();
}
