// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Abstractions.Plugins;
using DevProxy.Plugins.Behavior;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using Xunit;
using Xunit.Abstractions;

namespace DevProxy.Tests;

/// <summary>
/// Guards that each plugin's JSON schema in the current schema version folder matches
/// the plugin's configuration class, so configs that validate also work at runtime.
/// </summary>
public class PluginSchemaConsistencyTests(ITestOutputHelper output)
{
    private const string FileOnly = "Populated from a separate file the plugin loads, not from plugin configuration";

    /// <summary>
    /// Plugins that don't inherit <see cref="BasePlugin{TConfiguration}"/> and so have no configuration schema.
    /// </summary>
    private static readonly HashSet<string> _pluginsWithoutConfiguration =
    [
        "GraphBetaSupportGuidancePlugin",
        "GraphClientRequestIdGuidancePlugin",
        "GraphConnectorGuidancePlugin",
        "GraphMissingPermissionsGuidancePlugin",
        "GraphSdkGuidancePlugin",
        "GraphSelectGuidancePlugin",
        "JsonReporter",
        "MarkdownReporter",
        "MockGeneratorPlugin",
        "ODataPagingGuidancePlugin",
        "ODSPSearchGuidancePlugin",
        "OpenAIMockResponsePlugin",
        "OpenAIUsageDebuggingPlugin",
        "PlainTextReporter",
        "RetryAfterPlugin",
        "UrlDiscoveryPlugin",
    ];

    /// <summary>
    /// Plugins that reuse another plugin's configuration class and schema. They're
    /// covered by the comparison of the plugin they share with.
    /// </summary>
    private static readonly Dictionary<string, string> _pluginsSharingSchema = new()
    {
        ["EntraMockResponsePlugin"] = "MockResponsePlugin",
        ["GraphMockResponsePlugin"] = "MockResponsePlugin",
    };

    /// <summary>
    /// Known, intentional differences. This list may only shrink: fix the schema or
    /// the class and remove the entry, then lower <see cref="MaxAllowlistCount"/>.
    /// </summary>
    private static readonly Dictionary<(string Plugin, string Property, SchemaMismatchKind Kind), string> _allowlist = new()
    {
        [("ApiCenterMinimalPermissionsPlugin", "permissionsToExclude", SchemaMismatchKind.Default)] = "Default applied at runtime so an explicit null/[] can be told apart from a missing value",
        [("GraphMinimalPermissionsGuidancePlugin", "permissionsToExclude", SchemaMismatchKind.Default)] = "Default applied at runtime so an explicit null/[] can be told apart from a missing value",
        [("MinimalPermissionsGuidancePlugin", "permissionsToExclude", SchemaMismatchKind.Default)] = "Default applied at runtime so an explicit null/[] can be told apart from a missing value",
        [("MinimalPermissionsGuidancePlugin", "apiSpecsFolderPath", SchemaMismatchKind.Required)] = "Nullable so the plugin can report a missing value at startup",
        [("MinimalPermissionsPlugin", "apiSpecsFolderPath", SchemaMismatchKind.Required)] = "Nullable so the plugin can report a missing value at startup",
        [("AuthPlugin", "type", SchemaMismatchKind.Required)] = "Nullable so the plugin can report a missing value at startup",
        [("MinimalCsomPermissionsPlugin", "typesDefinitions", SchemaMismatchKind.MissingInSchema)] = FileOnly,
        [("CrudApiPlugin", "actions", SchemaMismatchKind.MissingInSchema)] = FileOnly,
        [("CrudApiPlugin", "apiKeyAuthConfig", SchemaMismatchKind.MissingInSchema)] = FileOnly,
        [("CrudApiPlugin", "auth", SchemaMismatchKind.MissingInSchema)] = FileOnly,
        [("CrudApiPlugin", "baseUrl", SchemaMismatchKind.MissingInSchema)] = FileOnly,
        [("CrudApiPlugin", "dataFile", SchemaMismatchKind.MissingInSchema)] = FileOnly,
        [("CrudApiPlugin", "enableCORS", SchemaMismatchKind.MissingInSchema)] = FileOnly,
        [("CrudApiPlugin", "entraAuthConfig", SchemaMismatchKind.MissingInSchema)] = FileOnly,
        [("MockRequestPlugin", "request", SchemaMismatchKind.MissingInSchema)] = FileOnly,
        [("MockResponsePlugin", "mocks", SchemaMismatchKind.MissingInSchema)] = FileOnly,
        [("MockResponsePlugin", "noMocks", SchemaMismatchKind.MissingInSchema)] = "Internal flag set by the --no-mocks option",
        [("MockResponsePlugin", "schema", SchemaMismatchKind.MissingInSchema)] = FileOnly,
        [("MockStdioResponsePlugin", "mocks", SchemaMismatchKind.MissingInSchema)] = FileOnly,
        [("MockStdioResponsePlugin", "noMocks", SchemaMismatchKind.MissingInSchema)] = "Internal flag set by the --no-mocks option",
        [("MockStdioResponsePlugin", "schema", SchemaMismatchKind.MissingInSchema)] = FileOnly,
        [("WebSocketMockResponsePlugin", "mocks", SchemaMismatchKind.MissingInSchema)] = FileOnly,
        [("WebSocketMockResponsePlugin", "noMocks", SchemaMismatchKind.MissingInSchema)] = "Internal flag set by the --no-mocks option",
        [("WebSocketMockResponsePlugin", "schema", SchemaMismatchKind.MissingInSchema)] = FileOnly,
        [("RewritePlugin", "rewrites", SchemaMismatchKind.MissingInSchema)] = FileOnly,
        [("OpenAITelemetryPlugin", "prices", SchemaMismatchKind.MissingInSchema)] = FileOnly,
        [("GenericRandomErrorPlugin", "errors", SchemaMismatchKind.MissingInSchema)] = FileOnly,
        [("LanguageModelRateLimitingPlugin", "customResponse", SchemaMismatchKind.MissingInSchema)] = FileOnly,
        [("RateLimitingPlugin", "customResponse", SchemaMismatchKind.MissingInSchema)] = FileOnly,
    };

    // Lower this when you remove allowlist entries. Never raise it.
    private const int MaxAllowlistCount = 29;

    private static readonly string _repoRoot = FindRepoRoot();
    private static readonly string _schemaFolder = Path.Combine(_repoRoot, "schemas", $"v{GetCurrentVersion()}");

    [Fact]
    public void Allowlist_OnlyShrinks()
    {
        Assert.True(_allowlist.Count <= MaxAllowlistCount,
            $"The schema mismatch allowlist has {_allowlist.Count} entries but may have at most {MaxAllowlistCount}. Fix the mismatch instead of allowlisting it.");
    }

    [Fact]
    public void PluginSchemas_MatchConfigurationClasses()
    {
        var plugins = typeof(LatencyPlugin).Assembly.GetTypes()
            .Where(t => t.IsPublic && t.IsClass && !t.IsAbstract && typeof(IPlugin).IsAssignableFrom(t))
            .OrderBy(t => t.Name, StringComparer.Ordinal)
            .ToArray();
        var byName = plugins.ToDictionary(p => p.Name, StringComparer.Ordinal);
        var failures = new List<string>();
        var mismatches = new List<SchemaMismatch>();
        var compared = 0;

        foreach (var plugin in plugins)
        {
            var configurationType = GetConfigurationType(plugin);
            if (configurationType is null)
            {
                if (!_pluginsWithoutConfiguration.Contains(plugin.Name))
                {
                    failures.Add($"{plugin.Name}: doesn't inherit BasePlugin<TConfiguration>. Add it to {nameof(_pluginsWithoutConfiguration)} if it has no configuration.");
                }

                continue;
            }

            if (_pluginsWithoutConfiguration.Contains(plugin.Name))
            {
                failures.Add($"{plugin.Name}: has configuration {configurationType.Name}; remove it from {nameof(_pluginsWithoutConfiguration)}.");
            }

            if (_pluginsSharingSchema.TryGetValue(plugin.Name, out var sharedWith))
            {
                if (!byName.TryGetValue(sharedWith, out var shared) || GetConfigurationType(shared) != configurationType)
                {
                    failures.Add($"{plugin.Name}: no longer shares its configuration class with {sharedWith}; give it its own schema.");
                }

                continue;
            }

            var schemaPath = Path.Combine(_schemaFolder, $"{plugin.Name.ToLowerInvariant()}.schema.json");
            if (!File.Exists(schemaPath))
            {
                failures.Add($"{plugin.Name}: has configuration {configurationType.Name} but no schema at {Path.GetRelativePath(_repoRoot, schemaPath)}.");
                continue;
            }

            var schema = JsonNode.Parse(File.ReadAllText(schemaPath))!.AsObject();
            mismatches.AddRange(PluginSchemaComparer.Compare(plugin.Name, configurationType, schema));
            compared++;
        }

        failures.AddRange(_pluginsWithoutConfiguration.Where(n => !byName.ContainsKey(n))
            .Select(n => $"{n}: listed in {nameof(_pluginsWithoutConfiguration)} but no such plugin exists."));
        failures.AddRange(_pluginsSharingSchema.Keys.Where(n => !byName.ContainsKey(n))
            .Select(n => $"{n}: listed in {nameof(_pluginsSharingSchema)} but no such plugin exists."));

        failures.AddRange(mismatches
            .Where(m => !_allowlist.ContainsKey((m.Plugin, m.Property, m.Kind)))
            .Select(m => m.ToString()));
        failures.AddRange(_allowlist.Keys
            .Where(k => !mismatches.Any(m => (m.Plugin, m.Property, m.Kind) == k))
            .Select(k => $"{k.Plugin}: property '{k.Property}' {k.Kind}: allowlisted but no longer mismatched. Remove it from the allowlist and lower {nameof(MaxAllowlistCount)}."));

        var expected = plugins.Length - _pluginsWithoutConfiguration.Count - _pluginsSharingSchema.Count;
        output.WriteLine($"Compared {compared} plugin/schema pairs in {Path.GetRelativePath(_repoRoot, _schemaFolder)} ({plugins.Length} plugins, {_pluginsWithoutConfiguration.Count} without configuration, {_pluginsSharingSchema.Count} sharing a schema).");

        Assert.True(failures.Count == 0, $"Plugin schemas don't match configuration classes:{Environment.NewLine}{string.Join(Environment.NewLine, failures)}");
        Assert.NotEqual(0, compared);
        Assert.Equal(expected, compared);
    }

    private static Type? GetConfigurationType(Type plugin)
    {
        for (var type = plugin.BaseType; type is not null; type = type.BaseType)
        {
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(BasePlugin<>))
            {
                return type.GetGenericArguments()[0];
            }
        }

        return null;
    }

    private static string GetCurrentVersion() =>
        XDocument.Load(Path.Combine(_repoRoot, "DevProxy", "DevProxy.csproj"))
            .Descendants("Version").First().Value;

    private static string FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "DevProxy.sln")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException($"Couldn't find DevProxy.sln above {AppContext.BaseDirectory}");
    }
}
