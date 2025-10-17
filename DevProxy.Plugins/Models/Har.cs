// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

namespace DevProxy.Plugins.Models;

internal sealed class HarFile
{
    public HarLog? Log { get; set; }
}

internal sealed class HarLog
{
    public string Version { get; set; } = "1.2";
    public HarCreator Creator { get; set; } = new();
    public List<HarEntry> Entries { get; set; } = [];
}

internal sealed class HarCreator
{
    public string? Name { get; set; }
    public string? Version { get; set; }
}

internal sealed class HarEntry
{
    public string? StartedDateTime { get; set; }
    public double Time { get; set; }
    public HarRequest? Request { get; set; }
    public HarResponse? Response { get; set; }
    public HarCache Cache { get; set; } = new();
    public HarTimings Timings { get; set; } = new();
}

internal sealed class HarRequest
{
    public string? Method { get; set; }
    public string? Url { get; set; }
    public string? HttpVersion { get; set; }
    public List<HarHeader> Headers { get; set; } = [];
    public List<HarQueryParam> QueryString { get; set; } = [];
    public List<HarCookie> Cookies { get; set; } = [];
    public long HeadersSize { get; set; }
    public long BodySize { get; set; }
    public HarPostData? PostData { get; set; }
}

internal sealed class HarResponse
{
    public int Status { get; set; }
    public string? StatusText { get; set; }
    public string? HttpVersion { get; set; }
    public List<HarHeader> Headers { get; set; } = [];
    public List<HarCookie> Cookies { get; set; } = [];
    public HarContent Content { get; set; } = new();
    public string RedirectURL { get; set; } = "";
    public long HeadersSize { get; set; }
    public long BodySize { get; set; }
}

internal sealed class HarHeader
{
    public string? Name { get; set; }
    public string? Value { get; set; }
}

internal sealed class HarQueryParam
{
    public string? Name { get; set; }
    public string? Value { get; set; }
}

internal sealed class HarCookie
{
    public string? Name { get; set; }
    public string? Value { get; set; }
    public string? Path { get; set; }
    public string? Domain { get; set; }
    public string? Expires { get; set; }
    public bool? HttpOnly { get; set; }
    public bool? Secure { get; set; }
}

internal sealed class HarPostData
{
    public string? MimeType { get; set; }
    public string? Text { get; set; }
    public List<HarParam>? Params { get; set; }
}

internal sealed class HarParam
{
    public string? Name { get; set; }
    public string? Value { get; set; }
    public string? FileName { get; set; }
    public string? ContentType { get; set; }
}

internal sealed class HarContent
{
    public long Size { get; set; }
    public string MimeType { get; set; } = "";
    public string? Text { get; set; }
    public string? Encoding { get; set; }
}

internal sealed class HarCache
{
    // Minimal - can be expanded if needed
}

internal sealed class HarTimings
{
    public double Send { get; set; }
    public double Wait { get; set; }
    public double Receive { get; set; }
}