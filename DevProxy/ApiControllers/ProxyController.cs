// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Abstractions.Proxy;
using DevProxy.Commands;
using DevProxy.Jwt;
using DevProxy.Proxy;
using Microsoft.AspNetCore.Mvc;
using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography.X509Certificates;

namespace DevProxy.ApiControllers;

[ApiController]
[Route("[controller]")]
#pragma warning disable CA1515 // required for the API controller
public sealed class ProxyController(IProxyStateController proxyStateController, IProxyConfiguration proxyConfiguration, ILoggerFactory loggerFactory) : ControllerBase
#pragma warning restore CA1515
{
    private readonly IProxyStateController _proxyStateController = proxyStateController;
    private readonly IProxyConfiguration _proxyConfiguration = proxyConfiguration;
    private readonly ILoggerFactory _loggerFactory = loggerFactory;

    [HttpGet]
    public ProxyInfo Get() => ProxyInfo.From(_proxyStateController.ProxyState, _proxyConfiguration);

    [HttpPost]
    public async Task<IActionResult> SetAsync([FromBody] ProxyInfo proxyInfo, CancellationToken cancellationToken)
    {
        if (proxyInfo == null)
        {
            ModelState.AddModelError("ProxyInfo", "ProxyInfo cannot be null");
            return ValidationProblem(ModelState);
        }

        if (proxyInfo.ConfigFile != null)
        {
            ModelState.AddModelError("ConfigFile", "ConfigFile cannot be set");
            return ValidationProblem(ModelState);
        }

        if (proxyInfo.Recording.HasValue)
        {
            if (proxyInfo.Recording.Value)
            {
                _proxyStateController.StartRecording();
            }
            else
            {
                await _proxyStateController.StopRecordingAsync(cancellationToken);
            }
        }

        return Ok(ProxyInfo.From(_proxyStateController.ProxyState, _proxyConfiguration));
    }

    [HttpPost("mockRequest")]
#pragma warning disable CA1030
    public async Task RaiseMockRequestAsync(CancellationToken cancellationToken)
#pragma warning restore CA1030
    {
        await _proxyStateController.MockRequestAsync(cancellationToken);
        Response.StatusCode = 202;
    }

    [HttpPost("stopProxy")]
    public void StopProxy()
    {
        Response.StatusCode = 202;
        _proxyStateController.StopProxy();
    }

    [HttpPost("jwtToken")]
    public IActionResult CreateJwtToken([FromBody] JwtOptions jwtOptions)
    {
        if (jwtOptions == null)
        {
            var problemDetails = new ProblemDetails
            {
                Title = "Invalid Request",
                Detail = "JWT options cannot be null.",
                Status = StatusCodes.Status400BadRequest
            };
            return BadRequest(problemDetails);
        }

        if (jwtOptions.SigningKey != null && jwtOptions.SigningKey.Length < 32)
        {
            var problemDetails = new ProblemDetails
            {
                Title = "Key too short",
                Detail = "The specified signing key is too short. A signing key must be at least 32 characters.",
                Status = StatusCodes.Status400BadRequest
            };
            return BadRequest(problemDetails);
        }

        var token = JwtTokenGenerator.CreateToken(jwtOptions);

        return Ok(new JwtInfo { Token = token });
    }

    [HttpGet("rootCertificate")]
    public IActionResult GetRootCertificate([FromQuery][Required] string format)
    {
        if (string.IsNullOrWhiteSpace(format))
        {
            ModelState.AddModelError("format", "Format is required.");
            return ValidationProblem(ModelState);
        }

        if (!format.Equals("crt", StringComparison.OrdinalIgnoreCase))
        {
            ModelState.AddModelError("format", "Invalid format. Supported format is 'crt'.");
            return ValidationProblem(ModelState);
        }

        // Ensure ProxyServer is initialized with LoggerFactory for Unobtanium logging
        ProxyEngine.EnsureProxyServerInitialized(_loggerFactory);

        var certificate = ProxyEngine.ProxyServer.CertificateManager.RootCertificate;
        if (certificate == null)
        {
            var problemDetails = new ProblemDetails
            {
                Title = "Certificate Not Found",
                Detail = "No root certificate found.",
                Status = StatusCodes.Status404NotFound
            };
            return NotFound(problemDetails);
        }

        var certBytes = certificate.Export(X509ContentType.Cert);
        var base64Cert = Convert.ToBase64String(certBytes, Base64FormattingOptions.InsertLineBreaks);
        var pem = "-----BEGIN CERTIFICATE-----\n" + base64Cert + "\n-----END CERTIFICATE-----";
        var pemBytes = System.Text.Encoding.ASCII.GetBytes(pem);

        return File(pemBytes, "application/x-x509-ca-cert", "devProxy.pem");
    }

    [HttpGet("logs")]
    public async Task GetLogsAsync(
        [FromQuery] int? lines,
        [FromQuery] bool follow = false,
        [FromQuery] string? since = null,
        CancellationToken cancellationToken = default)
    {
        // Only available in detached/daemon mode
        if (!DevProxyCommand.IsInternalDaemon)
        {
            Response.StatusCode = StatusCodes.Status404NotFound;
            await Response.WriteAsync("Logs endpoint is only available in detached mode.", cancellationToken);
            return;
        }

        var logFile = DevProxyCommand.DetachedLogFilePath;
        if (string.IsNullOrEmpty(logFile) || !System.IO.File.Exists(logFile))
        {
            Response.StatusCode = StatusCodes.Status404NotFound;
            await Response.WriteAsync("Log file not found.", cancellationToken);
            return;
        }

        var acceptHeader = Request.Headers.Accept.ToString();
        var useJson = acceptHeader.Contains("application/json", StringComparison.OrdinalIgnoreCase);
        var useSse = acceptHeader.Contains("text/event-stream", StringComparison.OrdinalIgnoreCase) || follow;

        if (useSse)
        {
            Response.ContentType = "text/event-stream";
            Response.Headers.CacheControl = "no-cache";
            Response.Headers.Connection = "keep-alive";

            await StreamLogsAsync(logFile, lines ?? 50, since, useJson, cancellationToken);
        }
        else
        {
            Response.ContentType = useJson ? "application/json" : "text/plain";
            await WriteLogsAsync(logFile, lines ?? 50, since, useJson, cancellationToken);
        }
    }

    private async Task WriteLogsAsync(string logFile, int lineCount, string? since, bool useJson, CancellationToken cancellationToken)
    {
        var allLines = await ReadAllLinesAsync(logFile, cancellationToken);
        var filteredLines = FilterLines(allLines, since).TakeLast(lineCount).ToList();

        if (useJson)
        {
            var logEntries = filteredLines.Select(ParseLogLine).ToList();
            var json = System.Text.Json.JsonSerializer.Serialize(logEntries);
            await Response.WriteAsync(json, cancellationToken);
        }
        else
        {
            foreach (var line in filteredLines)
            {
                await Response.WriteAsync(line + Environment.NewLine, cancellationToken);
            }
        }
    }

    private async Task StreamLogsAsync(string logFile, int initialLines, string? since, bool useJson, CancellationToken cancellationToken)
    {
        // Write initial lines
        var allLines = await ReadAllLinesAsync(logFile, cancellationToken);
        var filteredLines = FilterLines(allLines, since).TakeLast(initialLines).ToList();

        foreach (var line in filteredLines)
        {
            await WriteSseEventAsync(line, useJson, cancellationToken);
        }

        // Follow new lines
        var lastPosition = new FileInfo(logFile).Length;

        while (!cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(500, cancellationToken);

            try
            {
                using var fs = new FileStream(logFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                if (fs.Length > lastPosition)
                {
                    _ = fs.Seek(lastPosition, SeekOrigin.Begin);
                    using var reader = new StreamReader(fs);

                    string? line;
                    while ((line = await reader.ReadLineAsync(cancellationToken)) != null)
                    {
                        await WriteSseEventAsync(line, useJson, cancellationToken);
                    }

                    lastPosition = fs.Length;
                }
            }
            catch (IOException)
            {
                // File might be temporarily locked
            }
        }
    }

    private async Task WriteSseEventAsync(string line, bool useJson, CancellationToken cancellationToken)
    {
        if (useJson)
        {
            var logEntry = ParseLogLine(line);
            var json = System.Text.Json.JsonSerializer.Serialize(logEntry);
            await Response.WriteAsync($"event: log\ndata: {json}\n\n", cancellationToken);
        }
        else
        {
            await Response.WriteAsync($"data: {line}\n\n", cancellationToken);
        }

        await Response.Body.FlushAsync(cancellationToken);
    }

    private static IEnumerable<string> FilterLines(IList<string> lines, string? since)
    {
        if (string.IsNullOrEmpty(since))
        {
            return lines;
        }

        var sinceTime = ParseSinceOption(since);
        if (sinceTime == null)
        {
            return lines;
        }

        return lines.Where(line => LineMatchesSince(line, sinceTime.Value));
    }

    private static DateTime? ParseSinceOption(string? since)
    {
        if (string.IsNullOrEmpty(since))
        {
            return null;
        }

        // Try parsing as a duration (e.g., "5m", "1h", "30s")
        if (since.Length >= 2)
        {
            var unit = since[^1];
            if (int.TryParse(since[..^1], out var value))
            {
                return unit switch
                {
                    's' => DateTime.Now.AddSeconds(-value),
                    'm' => DateTime.Now.AddMinutes(-value),
                    'h' => DateTime.Now.AddHours(-value),
                    'd' => DateTime.Now.AddDays(-value),
                    _ => null
                };
            }
        }

        // Try parsing as a datetime
        if (DateTime.TryParse(since, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var dateTime))
        {
            return dateTime;
        }

        return null;
    }

    private static bool LineMatchesSince(string line, DateTime sinceTime)
    {
        // Parse timestamp from line format: [HH:mm:ss.fff] ...
        if (line.Length < 14 || line[0] != '[' || line[13] != ']')
        {
            return true;
        }

        var timestampStr = line[1..13];
        if (TimeSpan.TryParseExact(timestampStr, "hh\\:mm\\:ss\\.fff", System.Globalization.CultureInfo.InvariantCulture, out var timeOfDay))
        {
            var lineTime = DateTime.Today.Add(timeOfDay);
            if (lineTime > DateTime.Now)
            {
                lineTime = lineTime.AddDays(-1);
            }

            return lineTime >= sinceTime;
        }

        return true;
    }

    private static LogEntryDto ParseLogLine(string line)
    {
        // Parse: [HH:mm:ss.fff] level: category: message
        var entry = new LogEntryDto { Raw = line };

        if (line.Length < 14 || line[0] != '[' || line[13] != ']')
        {
            entry.Message = line;
            return entry;
        }

        entry.Time = line[1..13];

        if (line.Length < 16)
        {
            entry.Message = line;
            return entry;
        }

        var rest = line[15..]; // Skip "] "
        var colonIndex = rest.IndexOf(':', StringComparison.Ordinal);
        if (colonIndex > 0)
        {
            entry.Level = rest[..colonIndex].Trim();
            rest = rest[(colonIndex + 1)..].TrimStart();

            colonIndex = rest.IndexOf(':', StringComparison.Ordinal);
            if (colonIndex > 0)
            {
                entry.Category = rest[..colonIndex].Trim();
                entry.Message = rest[(colonIndex + 1)..].TrimStart();
            }
            else
            {
                entry.Message = rest;
            }
        }
        else
        {
            entry.Message = rest;
        }

        return entry;
    }

    private sealed class LogEntryDto
    {
        public string? Time { get; set; }
        public string? Level { get; set; }
        public string? Category { get; set; }
        public string? Message { get; set; }
        public string? Raw { get; set; }
    }

    private static async Task<List<string>> ReadAllLinesAsync(string filePath, CancellationToken cancellationToken)
    {
        var lines = new List<string>();
        using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(fs);

        string? line;
        while ((line = await reader.ReadLineAsync(cancellationToken)) != null)
        {
            lines.Add(line);
        }

        return lines;
    }
}