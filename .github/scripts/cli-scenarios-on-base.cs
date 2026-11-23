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

    foreach (var (path, changedLines) in GetChangedLines(baseRef, headRef))
    {
        var source = Git("show", $"{headRef}:{path}");
        var root = CSharpSyntaxTree.ParseText(source, path: path).GetRoot();
        foreach (var method in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
        {
            var testAttribute = method.AttributeLists.SelectMany(l => l.Attributes).FirstOrDefault(IsTestAttribute);
            if (testAttribute is null)
            {
                continue;
            }
            // The span covers the attributes and the body, so new [InlineData] rows count too.
            var span = method.GetLocation().GetLineSpan();
            var first = span.StartLinePosition.Line + 1;
            var last = span.EndLinePosition.Line + 1;
            if (!changedLines.Any(l => l >= first && l <= last))
            {
                continue;
            }
            var isSkipped = testAttribute.ArgumentList?.Arguments
                .Any(a => a.NameEquals?.Name.Identifier.Text == "Skip") == true;
            _ = (isSkipped ? skipped : selected).Add(GetFullyQualifiedName(method));
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
    if (headResults is null)
    {
        errors.Add("The scenarios didn't produce results against the PR build.");
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
        else if (headOutcomes.All(o => o == "Passed"))
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

static List<string> Lookup(Dictionary<string, List<string>>? results, string test) =>
    results is not null && results.TryGetValue(test, out var outcomes) ? outcomes : [];

// Maps fully qualified test method names to the outcome of each of their test cases.
static Dictionary<string, List<string>>? ReadTrx(string path)
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
    }
    return results;
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

// Returns, per changed .cs file under DevProxy.Cli.Tests, the head-side line numbers
// that were added or changed. A pure deletion marks the lines on both sides of it.
static Dictionary<string, HashSet<int>> GetChangedLines(string baseRef, string headRef)
{
    var diff = Git("diff", "--unified=0", "--no-color", "--no-ext-diff", "--find-renames", "--diff-filter=AMR",
        baseRef, headRef, "--", "DevProxy.Cli.Tests/");
    var files = new Dictionary<string, HashSet<int>>(StringComparer.Ordinal);
    HashSet<int>? current = null;
    var hunk = new Regex(@"^@@ -\d+(?:,\d+)? \+(\d+)(?:,(\d+))? @@");
    foreach (var line in diff.Split('\n'))
    {
        if (line.StartsWith("+++ ", StringComparison.Ordinal))
        {
            var path = line[4..].TrimEnd('\r');
            current = path.StartsWith("b/", StringComparison.Ordinal) && path.EndsWith(".cs", StringComparison.Ordinal)
                ? files[path[2..]] = []
                : null;
            continue;
        }
        var match = hunk.Match(line);
        if (current is null || !match.Success)
        {
            continue;
        }
        var start = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        var count = match.Groups[2].Success ? int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture) : 1;
        if (count == 0)
        {
            _ = current.Add(start);
            _ = current.Add(start + 1);
        }
        for (var i = 0; i < count; i++)
        {
            _ = current.Add(start + i);
        }
    }
    return files;
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

static class Files
{
    public const string Tests = "tests.txt";
    public const string Skipped = "skipped.txt";
    public const string Filter = "filter.txt";
    public const string BaseTrx = "base.trx";
    public const string HeadTrx = "head.trx";
}
