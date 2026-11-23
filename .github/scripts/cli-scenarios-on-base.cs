#!/usr/bin/env dotnet
// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

// Checks that the CLI scenario tests a PR adds or changes actually test the change:
// they must fail against the base build and pass against the PR build.
//
//   dotnet run .github/scripts/cli-scenarios-on-base.cs -- select <base> <head> <work-dir>
//     Writes tests.txt (fully qualified test methods) and filter.txt (dotnet test --filter)
//     for [Fact]/[Theory] methods in DevProxy.Cli.Tests whose lines changed between
//     <base> and <head>.
//
//   dotnet run .github/scripts/cli-scenarios-on-base.cs -- report <work-dir> <base-build-outcome> <allow-pass-on-base>
//     Reads base.trx and head.trx from <work-dir>, writes the job summary and exits
//     non-zero when a test passes on base, doesn't pass on the PR build, or the result
//     is inconclusive.

#:package Microsoft.CodeAnalysis.CSharp@4.14.0

using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

// Thrown by DevProxy.Cli.Tests when devproxy can't be found or started.
const string LaunchExceptionType = "DevProxy.Cli.Tests.CliLaunchException";

return args switch
{
    ["select", var baseRef, var headRef, var workDir] => Select(baseRef, headRef, workDir),
    ["report", var workDir, var baseBuildOutcome, var allowPassOnBase] =>
        Report(workDir, baseBuildOutcome, bool.Parse(allowPassOnBase)),
    _ => Usage(),
};

static int Usage()
{
    Console.Error.WriteLine("Usage: select <base> <head> <work-dir> | report <work-dir> <base-build-outcome> <allow-pass-on-base>");
    return 2;
}

static int Select(string baseRef, string headRef, string workDir)
{
    _ = Directory.CreateDirectory(workDir);
    var selected = new SortedSet<string>(StringComparer.Ordinal);
    var skipped = new SortedSet<string>(StringComparer.Ordinal);

    foreach (var file in GetChangedFiles(baseRef, headRef))
    {
        var headTests = GetTestMethods(Git("show", $"{headRef}:{file.HeadPath}"));
        var changed = headTests.Where(t => t.Overlaps(file.HeadLines)).Select(t => t.Name).ToHashSet(StringComparer.Ordinal);
        // Lines removed from a test leave no head-side lines inside it, so find those
        // tests on the base side. Tests that no longer exist on head are ignored.
        if (file.BasePath is not null && file.BaseLines.Count > 0)
        {
            changed.UnionWith(GetTestMethods(Git("show", $"{baseRef}:{file.BasePath}"))
                .Where(t => t.Overlaps(file.BaseLines))
                .Select(t => t.Name));
        }
        foreach (var test in headTests.Where(t => changed.Contains(t.Name)))
        {
            _ = (test.IsSkipped ? skipped : selected).Add(test.Name);
        }
    }

    File.WriteAllLines(Path.Combine(workDir, Files.Tests), selected);
    File.WriteAllLines(Path.Combine(workDir, Files.Skipped), skipped);
    File.WriteAllText(Path.Combine(workDir, Files.Filter),
        string.Join('|', selected.Select(t => $"FullyQualifiedName={t}")));

    Console.WriteLine($"{selected.Count} new or changed CLI scenario(s) selected.");
    foreach (var test in selected)
    {
        Console.WriteLine($"  {test}");
    }
    foreach (var test in skipped)
    {
        Console.WriteLine($"  {test} (not checked: has Skip)");
    }

    if (selected.Count == 0)
    {
        var summary = new StringBuilder()
            .AppendLine("## CLI scenarios on base")
            .AppendLine()
            .AppendLine("**0 scenarios selected.** This PR doesn't add or change tests in `DevProxy.Cli.Tests`, so there's nothing to check.");
        AppendSkipped(summary, skipped);
        WriteSummary(summary.ToString());
    }

    WriteOutput("count", selected.Count.ToString(CultureInfo.InvariantCulture));
    return 0;
}

static int Report(string workDir, string baseBuildOutcome, bool allowPassOnBase)
{
    var tests = ReadLines(Path.Combine(workDir, Files.Tests));
    var skipped = ReadLines(Path.Combine(workDir, Files.Skipped));
    var baseResults = ReadTrx(Path.Combine(workDir, Files.BaseTrx));
    var headResults = ReadTrx(Path.Combine(workDir, Files.HeadTrx));

    var errors = new List<string>();
    var inconclusive = new List<string>();
    var rows = new List<string>();
    var failedOnBase = 0;
    var passedOnHead = 0;

    if (!allowPassOnBase && baseBuildOutcome != "success")
    {
        inconclusive.Add($"Building or starting the base `devproxy` failed (outcome: {baseBuildOutcome}), so it's unknown whether the scenarios fail on base.");
    }
    else if (!allowPassOnBase && baseResults is null)
    {
        inconclusive.Add("The scenarios didn't produce results against the base build.");
    }
    else if (!allowPassOnBase && baseResults?.Unusable is not null)
    {
        inconclusive.Add($"The scenarios didn't run to completion against the base build: {baseResults.Unusable}.");
    }
    if (headResults is null)
    {
        errors.Add("The scenarios didn't produce results against the PR build.");
    }
    else if (headResults.Unusable is not null)
    {
        errors.Add($"The scenarios didn't run to completion against the PR build: {headResults.Unusable}.");
    }
    var baseUsable = !allowPassOnBase && inconclusive.Count == 0;

    foreach (var test in tests)
    {
        string baseCell;
        if (allowPassOnBase)
        {
            baseCell = "not checked";
        }
        else if (!baseUsable)
        {
            baseCell = "⚠️ inconclusive";
        }
        else
        {
            var outcomes = Lookup(baseResults, test);
            if (outcomes.Count == 0)
            {
                baseCell = "⚠️ no result";
                inconclusive.Add($"`{test}` didn't run against the base build.");
            }
            else if (baseResults!.LaunchFailures.Contains(test))
            {
                baseCell = "⚠️ couldn't start devproxy";
                inconclusive.Add($"`{test}` couldn't start the base `devproxy`, so its failure says nothing about the change.");
            }
            else if (outcomes.Contains("Failed"))
            {
                baseCell = "✅ fails";
                failedOnBase++;
            }
            else
            {
                baseCell = $"❌ {Describe(outcomes)}";
                errors.Add($"`{test}` passes against the base build, so it doesn't show that this PR changes anything. Make it fail without the change, or add the `test-passes-on-main` label if it intentionally locks in existing behavior.");
            }
        }

        string headCell;
        var headOutcomes = Lookup(headResults, test);
        if (headOutcomes.Count == 0)
        {
            headCell = "❌ no result";
            if (headResults is not null)
            {
                errors.Add($"`{test}` didn't run against the PR build.");
            }
        }
        else if (headResults!.Unusable is null && headOutcomes.All(o => o == "Passed"))
        {
            headCell = "✅ passes";
            passedOnHead++;
        }
        else
        {
            headCell = $"❌ {Describe(headOutcomes)}";
            errors.Add($"`{test}` doesn't pass against the PR build.");
        }

        rows.Add($"| `{test}` | {baseCell} | {headCell} |");
    }

    var summary = new StringBuilder()
        .AppendLine("## CLI scenarios on base")
        .AppendLine();
    if (inconclusive.Count > 0)
    {
        _ = summary.AppendLine(CultureInfo.InvariantCulture, $"**Inconclusive.** {tests.Count} scenario(s) selected, but the check couldn't tell whether they fail on base.");
    }
    else if (allowPassOnBase)
    {
        _ = summary.AppendLine(CultureInfo.InvariantCulture, $"**{tests.Count} scenario(s) selected: {passedOnHead} pass on the PR build.** Not run against base because this PR has the `test-passes-on-main` label.");
    }
    else
    {
        _ = summary.AppendLine(CultureInfo.InvariantCulture, $"**{tests.Count} scenario(s) selected: {failedOnBase} fail on base, {passedOnHead} pass on the PR build.**");
    }
    _ = summary
        .AppendLine()
        .AppendLine("| Test | Base | PR |")
        .AppendLine("|---|---|---|");
    foreach (var row in rows)
    {
        _ = summary.AppendLine(row);
    }
    if (inconclusive.Count + errors.Count > 0)
    {
        _ = summary.AppendLine();
        foreach (var message in inconclusive.Select(m => $"Inconclusive: {m}").Concat(errors))
        {
            _ = summary.AppendLine(CultureInfo.InvariantCulture, $"- {message}");
        }
    }
    AppendSkipped(summary, skipped);
    _ = summary.AppendLine().AppendLine("A theory counts as failing on base when at least one of its cases fails.");
    WriteSummary(summary.ToString());

    foreach (var message in inconclusive)
    {
        Console.WriteLine($"::error title=CLI scenarios on base (inconclusive)::{message}");
    }
    foreach (var message in errors)
    {
        Console.WriteLine($"::error title=CLI scenarios on base::{message}");
    }
    return inconclusive.Count + errors.Count > 0 ? 1 : 0;
}

static List<string> ReadLines(string path) =>
    File.Exists(path) ? [.. File.ReadAllLines(path).Where(l => l.Length > 0)] : [];

static string Describe(List<string> outcomes) => string.Join(", ", outcomes
    .GroupBy(o => o)
    .Select(g => $"{g.Count()} {g.Key.ToLowerInvariant()}"));

static List<string> Lookup(TrxRun? run, string test) =>
    run is not null && run.Results.TryGetValue(test, out var outcomes) ? outcomes : [];

// Maps fully qualified test method names to the outcome of each of their test cases.
// Returns null when the file is missing.
static TrxRun? ReadTrx(string path)
{
    if (!File.Exists(path))
    {
        return null;
    }
    XNamespace ns = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";
    var doc = XDocument.Load(path);
    var methods = doc.Descendants(ns + "UnitTest").ToDictionary(
        t => (string)t.Attribute("id")!,
        t =>
        {
            var method = t.Element(ns + "TestMethod")!;
            return $"{(string)method.Attribute("className")!}.{(string)method.Attribute("name")!}";
        });
    var results = new Dictionary<string, List<string>>(StringComparer.Ordinal);
    var launchFailures = new HashSet<string>(StringComparer.Ordinal);
    foreach (var result in doc.Descendants(ns + "UnitTestResult"))
    {
        if (!methods.TryGetValue((string)result.Attribute("testId")!, out var name))
        {
            continue;
        }
        if (!results.TryGetValue(name, out var outcomes))
        {
            results[name] = outcomes = [];
        }
        outcomes.Add((string?)result.Attribute("outcome") ?? "Unknown");
        var message = (string?)result.Descendants(ns + "Message").FirstOrDefault() ?? "";
        if (message.Contains(LaunchExceptionType, StringComparison.Ordinal))
        {
            _ = launchFailures.Add(name);
        }
    }

    // A crashed or aborted test host leaves partial results that look like passes. xUnit
    // also logs every failing test as a run-level error, so look for VSTest's abort message.
    var summary = doc.Descendants(ns + "ResultSummary").FirstOrDefault();
    var summaryOutcome = (string?)summary?.Attribute("outcome");
    var runErrors = summary?.Descendants(ns + "RunInfo")
        .Where(r => (string?)r.Attribute("outcome") == "Error")
        .Select(r => ((string?)r.Element(ns + "Text"))?.Trim() ?? "")
        .Where(t => t.Contains("test run was aborted", StringComparison.OrdinalIgnoreCase)
            || t.Contains("test host process crashed", StringComparison.OrdinalIgnoreCase))
        .Select(t => t.ReplaceLineEndings(" ").Split(" at ", 2)[0].Trim())
        .ToList() ?? [];
    string? unusable = null;
    if (summaryOutcome is not ("Completed" or "Failed" or "Passed"))
    {
        unusable = $"the test run ended with outcome '{summaryOutcome ?? "missing"}'";
    }
    else if (runErrors.Count > 0)
    {
        unusable = string.Join(" ", runErrors).TrimEnd('.');
    }
    return new(results, launchFailures, unusable);
}

static List<TestMethod> GetTestMethods(string source)
{
    var tests = new List<TestMethod>();
    foreach (var method in CSharpSyntaxTree.ParseText(source).GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>())
    {
        var testAttribute = method.AttributeLists.SelectMany(l => l.Attributes).FirstOrDefault(IsTestAttribute);
        if (testAttribute is null)
        {
            continue;
        }
        // The span covers the attributes and the body, so new [InlineData] rows count too.
        var span = method.GetLocation().GetLineSpan();
        var isSkipped = testAttribute.ArgumentList?.Arguments
            .Any(a => a.NameEquals?.Name.Identifier.Text == "Skip") == true;
        tests.Add(new(GetFullyQualifiedName(method), span.StartLinePosition.Line + 1, span.EndLinePosition.Line + 1, isSkipped));
    }
    return tests;
}

static bool IsTestAttribute(AttributeSyntax attribute)
{
    var name = attribute.Name switch
    {
        QualifiedNameSyntax q => q.Right.Identifier.Text,
        AliasQualifiedNameSyntax a => a.Name.Identifier.Text,
        SimpleNameSyntax s => s.Identifier.Text,
        _ => attribute.Name.ToString(),
    };
    return name is "Fact" or "FactAttribute" or "Theory" or "TheoryAttribute";
}

// Matches the FullyQualifiedName VSTest reports for xUnit tests: Namespace.Outer+Nested.Method.
static string GetFullyQualifiedName(MethodDeclarationSyntax method)
{
    var types = method.Ancestors().OfType<BaseTypeDeclarationSyntax>().Select(t => t.Identifier.Text).Reverse();
    var namespaces = method.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().Select(n => n.Name.ToString()).Reverse();
    var prefix = string.Join('.', namespaces);
    return $"{(prefix.Length > 0 ? prefix + "." : "")}{string.Join('+', types)}.{method.Identifier.Text}";
}

// Returns the added/changed head-side lines and removed/changed base-side lines of each
// changed .cs file under DevProxy.Cli.Tests.
static List<ChangedFile> GetChangedFiles(string baseRef, string headRef)
{
    var diff = Git("diff", "--unified=0", "--no-color", "--no-ext-diff", "--find-renames", "--diff-filter=AMR",
        baseRef, headRef, "--", "DevProxy.Cli.Tests/");
    var files = new List<ChangedFile>();
    string? basePath = null;
    ChangedFile? current = null;
    var hunk = new Regex(@"^@@ -(\d+)(?:,(\d+))? \+(\d+)(?:,(\d+))? @@");
    foreach (var rawLine in diff.Split('\n'))
    {
        var line = rawLine.TrimEnd('\r');
        if (line.StartsWith("--- ", StringComparison.Ordinal))
        {
            basePath = line.StartsWith("--- a/", StringComparison.Ordinal) ? line[6..] : null;
            continue;
        }
        if (line.StartsWith("+++ ", StringComparison.Ordinal))
        {
            current = line.StartsWith("+++ b/", StringComparison.Ordinal) && line.EndsWith(".cs", StringComparison.Ordinal)
                ? new ChangedFile(line[6..], basePath)
                : null;
            if (current is not null)
            {
                files.Add(current);
            }
            continue;
        }
        var match = hunk.Match(line);
        if (current is null || !match.Success)
        {
            continue;
        }
        AddRange(current.BaseLines, match.Groups[1], match.Groups[2]);
        AddRange(current.HeadLines, match.Groups[3], match.Groups[4]);
    }
    return files;

    static void AddRange(HashSet<int> lines, Group start, Group count)
    {
        var first = int.Parse(start.Value, CultureInfo.InvariantCulture);
        var length = count.Success ? int.Parse(count.Value, CultureInfo.InvariantCulture) : 1;
        for (var i = 0; i < length; i++)
        {
            _ = lines.Add(first + i);
        }
    }
}

static string Git(params string[] arguments)
{
    var startInfo = new ProcessStartInfo("git")
    {
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        StandardOutputEncoding = Encoding.UTF8,
    };
    startInfo.ArgumentList.Add("-c");
    startInfo.ArgumentList.Add("core.quotePath=false");
    foreach (var argument in arguments)
    {
        startInfo.ArgumentList.Add(argument);
    }
    using var process = Process.Start(startInfo)!;
    var stdErrTask = process.StandardError.ReadToEndAsync();
    var output = process.StandardOutput.ReadToEnd();
    process.WaitForExit();
    return process.ExitCode == 0
        ? output
        : throw new InvalidOperationException($"git {string.Join(' ', arguments)} failed: {stdErrTask.Result}");
}

static void AppendSkipped(StringBuilder summary, IEnumerable<string> skipped)
{
    foreach (var test in skipped)
    {
        _ = summary.AppendLine().Append("- `").Append(test).AppendLine("` changed but has `Skip`, so it isn't checked.");
    }
}

static void WriteSummary(string markdown)
{
    var path = Environment.GetEnvironmentVariable("GITHUB_STEP_SUMMARY");
    if (string.IsNullOrEmpty(path))
    {
        Console.WriteLine(markdown);
    }
    else
    {
        File.AppendAllText(path, markdown + Environment.NewLine);
    }
}

static void WriteOutput(string name, string value)
{
    var path = Environment.GetEnvironmentVariable("GITHUB_OUTPUT");
    if (!string.IsNullOrEmpty(path))
    {
        File.AppendAllText(path, $"{name}={value}{Environment.NewLine}");
    }
}

sealed record TestMethod(string Name, int FirstLine, int LastLine, bool IsSkipped)
{
    public bool Overlaps(IEnumerable<int> lines) => lines.Any(l => l >= FirstLine && l <= LastLine);
}

sealed record ChangedFile(string HeadPath, string? BasePath)
{
    public HashSet<int> HeadLines { get; } = [];
    public HashSet<int> BaseLines { get; } = [];
}

sealed record TrxRun(Dictionary<string, List<string>> Results, HashSet<string> LaunchFailures, string? Unusable);

static class Files
{
    public const string Tests = "tests.txt";
    public const string Skipped = "skipped.txt";
    public const string Filter = "filter.txt";
    public const string BaseTrx = "base.trx";
    public const string HeadTrx = "head.trx";
}
