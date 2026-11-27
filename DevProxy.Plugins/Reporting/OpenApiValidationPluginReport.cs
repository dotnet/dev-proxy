// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Abstractions.Plugins;
using System.Globalization;
using System.Text;

namespace DevProxy.Plugins.Reporting;

public enum OpenApiValidationIssueLocation
{
    Request,
    Response
}

public sealed class OpenApiValidationIssue
{
    public required OpenApiValidationIssueLocation Location { get; init; }
    public required string Message { get; init; }
    /// <summary>
    /// Location of the issue in the request or response, e.g. the parameter
    /// name or a JSON path in the body. Empty when the issue applies to the
    /// whole request or response.
    /// </summary>
    public string? Path { get; init; }

    public override string ToString() =>
        string.IsNullOrEmpty(Path) ? Message : $"{Path}: {Message}";
}

public sealed class OpenApiValidationResult
{
    public required string Request { get; init; }
    public int? StatusCode { get; init; }
    /// <summary>
    /// Matched operation, e.g. <c>GET /users/{id}</c>. Empty when the request
    /// isn't documented in the spec.
    /// </summary>
    public string? Operation { get; init; }
    public string? SpecFile { get; init; }
    public required IEnumerable<OpenApiValidationIssue> Issues { get; init; }
}

public sealed class OpenApiValidationPluginReport : IMarkdownReport, IPlainTextReport
{
    public required int ValidatedRequests { get; init; }
    public required IEnumerable<OpenApiValidationResult> Results { get; init; }
    public IEnumerable<string>? UnmatchedRequests { get; init; }
    /// <summary>
    /// Parts of validated requests that weren't validated, e.g. bodies
    /// exceeding the maximum supported size, with the reason.
    /// </summary>
    public IEnumerable<string>? SkippedValidations { get; init; }

    public string? ToMarkdown()
    {
        var sb = new StringBuilder();
        _ = sb.AppendLine("# OpenAPI validation report")
            .AppendLine()
            .AppendLine(CultureInfo.InvariantCulture, $"- Validated requests: {ValidatedRequests}")
            .AppendLine(CultureInfo.InvariantCulture, $"- Requests with issues: {Results.Count()}");
        if (SkippedValidations?.Any() == true)
        {
            _ = sb.AppendLine(CultureInfo.InvariantCulture, $"- Skipped validations: {SkippedValidations.Count()}");
        }

        if (!Results.Any() && SkippedValidations?.Any() != true)
        {
            _ = sb.AppendLine()
                .AppendLine("✅ All validated requests and responses match their OpenAPI specs.");
        }

        foreach (var result in Results)
        {
            _ = sb.AppendLine()
                .AppendLine(CultureInfo.InvariantCulture, $"## {result.Request}")
                .AppendLine();

            if (result.StatusCode is not null)
            {
                _ = sb.AppendLine(CultureInfo.InvariantCulture, $"- Status code: {result.StatusCode}");
            }
            if (!string.IsNullOrEmpty(result.Operation))
            {
                _ = sb.AppendLine(CultureInfo.InvariantCulture, $"- Operation: `{result.Operation}`");
            }
            if (!string.IsNullOrEmpty(result.SpecFile))
            {
                _ = sb.AppendLine(CultureInfo.InvariantCulture, $"- Spec: {result.SpecFile}");
            }

            _ = sb.AppendLine()
                .AppendLine("|Location|Path|Issue|")
                .AppendLine("|--------|----|-----|");
            foreach (var issue in result.Issues)
            {
                var issuePath = string.IsNullOrEmpty(issue.Path) ? "" : $"`{issue.Path}`";
                _ = sb.AppendLine(CultureInfo.InvariantCulture,
                    $"|{issue.Location.ToString().ToLowerInvariant()}|{issuePath}|{EscapeTableCell(issue.Message)}|");
            }
        }

        if (SkippedValidations?.Any() == true)
        {
            _ = sb.AppendLine()
                .AppendLine("## Skipped validations")
                .AppendLine()
                .AppendJoin(Environment.NewLine, SkippedValidations.Select(r => $"- {r}"))
                .AppendLine();
        }

        if (UnmatchedRequests?.Any() == true)
        {
            _ = sb.AppendLine()
                .AppendLine("## Requests not matching any OpenAPI spec")
                .AppendLine()
                .AppendJoin(Environment.NewLine, UnmatchedRequests.Select(r => $"- {r}"))
                .AppendLine();
        }

        return sb.ToString();
    }

    public string? ToPlainText()
    {
        var sb = new StringBuilder();
        _ = sb.AppendLine("OpenAPI validation report")
            .AppendLine()
            .AppendLine(CultureInfo.InvariantCulture, $"Validated requests: {ValidatedRequests}")
            .AppendLine(CultureInfo.InvariantCulture, $"Requests with issues: {Results.Count()}");
        if (SkippedValidations?.Any() == true)
        {
            _ = sb.AppendLine(CultureInfo.InvariantCulture, $"Skipped validations: {SkippedValidations.Count()}");
        }

        foreach (var result in Results)
        {
            _ = sb.AppendLine()
                .AppendLine(result.Request);

            if (result.StatusCode is not null)
            {
                _ = sb.AppendLine(CultureInfo.InvariantCulture, $"Status code: {result.StatusCode}");
            }
            if (!string.IsNullOrEmpty(result.Operation))
            {
                _ = sb.AppendLine(CultureInfo.InvariantCulture, $"Operation: {result.Operation}");
            }
            if (!string.IsNullOrEmpty(result.SpecFile))
            {
                _ = sb.AppendLine(CultureInfo.InvariantCulture, $"Spec: {result.SpecFile}");
            }

            foreach (var issue in result.Issues)
            {
                _ = sb.AppendLine(CultureInfo.InvariantCulture,
                    $"- [{issue.Location.ToString().ToLowerInvariant()}] {issue}");
            }
        }

        if (SkippedValidations?.Any() == true)
        {
            _ = sb.AppendLine()
                .AppendLine("Skipped validations:")
                .AppendLine()
                .AppendJoin(Environment.NewLine, SkippedValidations.Select(r => $"- {r}"))
                .AppendLine();
        }

        if (UnmatchedRequests?.Any() == true)
        {
            _ = sb.AppendLine()
                .AppendLine("Requests not matching any OpenAPI spec:")
                .AppendLine()
                .AppendJoin(Environment.NewLine, UnmatchedRequests.Select(r => $"- {r}"))
                .AppendLine();
        }

        return sb.ToString();
    }

    private static string EscapeTableCell(string value) =>
        value.Replace("|", "\\|", StringComparison.Ordinal);
}
