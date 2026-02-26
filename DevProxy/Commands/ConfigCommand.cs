// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Abstractions.Plugins;
using DevProxy.Abstractions.Proxy;
using DevProxy.Abstractions.Utils;
using DevProxy.Logging;
using DevProxy.Plugins;
using System.CommandLine;
using System.CommandLine.Parsing;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using YamlDotNet.Core;

namespace DevProxy.Commands;

sealed class ProxyConfigInfo
{
    public IList<string> ConfigFiles { get; set; } = [];
    public IList<string> MockFiles { get; set; } = [];
}

sealed class GitHubTreeResponse
{
    public GitHubTreeItem[] Tree { get; set; } = [];
    public bool Truncated { get; set; }
}

sealed class GitHubTreeItem
{
    public string Path { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
}

sealed class VisualStudioCodeSnippet
{
    public string? Prefix { get; set; }
    public string[]? Body { get; set; }
    public string? Description { get; set; }
}

sealed class ConfigCommand : Command
{
    private readonly ILogger _logger;
    private readonly IProxyConfiguration _proxyConfiguration;
    private readonly HttpClient _httpClient;
    private readonly string snippetsFileUrl = $"https://aka.ms/devproxy/snippets/v{ProxyUtils.NormalizeVersion(ProxyUtils.ProductVersion)}";
    private readonly string configFileSnippetName = "ConfigFile";
    public ConfigCommand(
        HttpClient httpClient,
        IProxyConfiguration proxyConfiguration,
        ILogger<ConfigCommand> logger) :
        base("config", "Manage Dev Proxy configs")
    {
        _proxyConfiguration = proxyConfiguration;
        _logger = logger;
        _httpClient = httpClient;
        _httpClient.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("dev-proxy", ProxyUtils.ProductVersion));
        ConfigureCommand();
    }

    /// <summary>
    /// Runs config validation standalone, without requiring the full DI container.
    /// Used when the proxy is invoked with 'config validate' to allow validating
    /// even broken config files.
    /// </summary>
    internal static async Task<int> RunValidateStandaloneAsync(string[] args)
    {
        string? configFile = null;
        var isJsonOutput = false;

        for (var i = 0; i < args.Length; i++)
        {
            if ((string.Equals(args[i], "--config-file", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(args[i], "-c", StringComparison.OrdinalIgnoreCase)) &&
                i + 1 < args.Length)
            {
                configFile = args[i + 1];
                i++;
            }
            else if (string.Equals(args[i], DevProxyCommand.OutputOptionName, StringComparison.OrdinalIgnoreCase) &&
                     i + 1 < args.Length)
            {
                isJsonOutput = string.Equals(args[i + 1], "json", StringComparison.OrdinalIgnoreCase);
                i++;
            }
        }

        using var httpClient = new HttpClient();
        httpClient.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue("dev-proxy", ProxyUtils.ProductVersion));

        var formatterName = isJsonOutput
            ? JsonConsoleFormatter.FormatterName
            : ProxyConsoleFormatter.DefaultCategoryName;

        using var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder
                .SetMinimumLevel(LogLevel.Information)
                .AddConsole(consoleOptions =>
                {
                    consoleOptions.FormatterName = formatterName;
                    consoleOptions.LogToStandardErrorThreshold = LogLevel.Warning;
                })
                .AddConsoleFormatter<ProxyConsoleFormatter, ProxyConsoleFormatterOptions>(formatterOptions =>
                {
                    formatterOptions.IncludeScopes = false;
                    formatterOptions.ShowSkipMessages = true;
                    formatterOptions.ShowTimestamps = false;
                })
                .AddConsoleFormatter<JsonConsoleFormatter, ProxyConsoleFormatterOptions>(formatterOptions =>
                {
                    formatterOptions.IncludeScopes = false;
                    formatterOptions.ShowSkipMessages = true;
                    formatterOptions.ShowTimestamps = false;
                });
        });
        var logger = loggerFactory.CreateLogger<ConfigCommand>();

        return await ValidateConfigCoreAsync(configFile, isJsonOutput, httpClient, logger, CancellationToken.None);
    }

    private void ConfigureCommand()
    {
        var configGetCommand = new Command("get", "Download the specified config from the Sample Solution Gallery");
        var configIdArgument = new Argument<string>("config-id")
        {
            Description = "The ID of the config to download"
        };
        configGetCommand.Add(configIdArgument);
        configGetCommand.SetAction(async (parseResult) =>
        {
            var configId = parseResult.GetValue(configIdArgument);
            var outputFormat = parseResult.GetValueOrDefault<OutputFormat?>(DevProxyCommand.OutputOptionName) ?? OutputFormat.Text;
            if (configId != null)
            {
                await DownloadConfigAsync(configId, outputFormat);
            }
        });

        var configNewCommand = new Command("new", "Create new Dev Proxy configuration file");
        var nameArgument = new Argument<string>("name")
        {
            Arity = ArgumentArity.ZeroOrOne,
            DefaultValueFactory = _ => "devproxyrc.json",
            Description = "Name of the configuration file"
        };
        configNewCommand.Add(nameArgument);
        configNewCommand.SetAction(async (parseResult) =>
        {
            var name = parseResult.GetValue(nameArgument) ?? "devproxyrc.json";
            var outputFormat = parseResult.GetValueOrDefault<OutputFormat?>(DevProxyCommand.OutputOptionName) ?? OutputFormat.Text;
            await CreateConfigFileAsync(name, outputFormat);
        });

        var configOpenCommand = new Command("open", "Open devproxyrc.json");
        configOpenCommand.SetAction(parseResult =>
        {
            var cfgPsi = new ProcessStartInfo(_proxyConfiguration.ConfigFile)
            {
                UseShellExecute = true
            };
            _ = Process.Start(cfgPsi);
        });

        var configValidateCommand = new Command("validate", "Validate a Dev Proxy configuration file");
        var validateConfigFileOption = new Option<string?>("--config-file", "-c")
        {
            Description = "The path to the configuration file to validate",
            HelpName = "config-file"
        };
        configValidateCommand.Add(validateConfigFileOption);
        configValidateCommand.SetAction(async (parseResult, cancellationToken) =>
        {
            var configFile = parseResult.GetValue(validateConfigFileOption);
            var output = parseResult.GetValueOrDefault<OutputFormat?>(DevProxyCommand.OutputOptionName);
            var isJsonOutput = output == OutputFormat.Json;
            return await ValidateConfigCoreAsync(configFile, isJsonOutput, _httpClient, _logger, cancellationToken);
        });

        this.AddCommands(new List<Command>
        {
            configGetCommand,
            configNewCommand,
            configOpenCommand,
            configValidateCommand
        }.OrderByName());

        HelpExamples.Add(this, [
            "devproxy config new                                 Create default devproxyrc.json",
            "devproxy config new myconfig.json                   Create named config file",
            "devproxy config get <config-id>                     Download config from gallery",
            "devproxy config open                                Open config in default editor",
        ]);
    }

    private async Task DownloadConfigAsync(string configId, OutputFormat outputFormat)
    {
        try
        {
            var appFolder = ProxyUtils.AppFolder;
            if (string.IsNullOrEmpty(appFolder) || !Directory.Exists(appFolder))
            {
                if (outputFormat == OutputFormat.Text)
                {
                    _logger.LogError("App folder {AppFolder} not found", appFolder);
                }
                return;
            }

            var configFolderPath = Path.Combine(appFolder, "config");
            _logger.LogDebug("Checking if config folder {ConfigFolderPath} exists...", configFolderPath);
            if (!Directory.Exists(configFolderPath))
            {
                _logger.LogDebug("Config folder not found, creating it...");
                _ = Directory.CreateDirectory(configFolderPath);
                _logger.LogDebug("Config folder created");
            }

            _logger.LogDebug("Getting target folder path for config {ConfigId}...", configId);
            var targetFolderPath = GetTargetFolderPath(appFolder, configId);
            _logger.LogDebug("Creating target folder {TargetFolderPath}...", targetFolderPath);
            _ = Directory.CreateDirectory(targetFolderPath);

            if (outputFormat == OutputFormat.Text)
            {
                _logger.LogInformation("Downloading config {ConfigId}...", configId);
            }

            var sampleFiles = await GetFilesToDownloadAsync(configId);
            if (sampleFiles.Length == 0)
            {
                if (outputFormat == OutputFormat.Text)
                {
                    _logger.LogError("Config {ConfigId} not found in the samples repo", configId);
                }
                return;
            }
            foreach (var sampleFile in sampleFiles)
            {
                await DownloadFileAsync(sampleFile, targetFolderPath, configId);
            }

            var configInfo = GetConfigInfo(targetFolderPath);

            if (outputFormat == OutputFormat.Json)
            {
                var json = JsonSerializer.Serialize(new
                {
                    configId,
                    path = targetFolderPath,
                    configFiles = configInfo.ConfigFiles,
                    mockFiles = configInfo.MockFiles
                }, ProxyUtils.JsonSerializerOptions);
                _logger.LogStructuredOutput(json);
                return;
            }

            _logger.LogInformation("Config saved in {TargetFolderPath}\r\n", targetFolderPath);

            if (!configInfo.ConfigFiles.Any() && !configInfo.MockFiles.Any())
            {
                return;
            }

            if (configInfo.ConfigFiles.Any())
            {
                _logger.LogInformation("To start Dev Proxy with the config, run:");
                foreach (var configFile in configInfo.ConfigFiles)
                {
                    if (_logger.IsEnabled(LogLevel.Information))
                    {
                        _logger.LogInformation("  devproxy --config-file \"{ConfigFile}\"", configFile.Replace(appFolder, "~appFolder", StringComparison.OrdinalIgnoreCase));
                    }
                }
            }
            else
            {
                _logger.LogInformation("To start Dev Proxy with the mock file, enable the MockResponsePlugin or GraphMockResponsePlugin and run:");
                foreach (var mockFile in configInfo.MockFiles)
                {
                    if (_logger.IsEnabled(LogLevel.Information))
                    {
                        _logger.LogInformation("  devproxy --mock-file \"{MockFile}\"", mockFile.Replace(appFolder, "~appFolder", StringComparison.OrdinalIgnoreCase));
                    }
                }
            }
        }
        catch (Exception ex)
        {
            if (outputFormat == OutputFormat.Text)
            {
                _logger.LogError(ex, "Error downloading config");
            }
        }
    }

    /// <summary>
    /// Returns the list of files that can be used as entry points for the config
    /// </summary>
    /// <remarks>
    /// A sample in the gallery can have multiple entry points. It can
    /// contain multiple config files or no config files and a multiple
    /// mock files. This method returns the list of files that Dev Proxy
    /// can use as entry points.
    /// If there's one or more config files, it'll return an array of
    /// these file names. If there are no proxy configs, it'll return
    /// an array of all the mock files. If there are no mocks, it'll return
    /// an empty array indicating that there's no entry point.
    /// </remarks>
    /// <param name="configFolder">Full path to the folder with config files</param>
    /// <returns>Array of files that can be used to start proxy with</returns>
    private ProxyConfigInfo GetConfigInfo(string configFolder)
    {
        var configInfo = new ProxyConfigInfo();

        _logger.LogDebug("Getting list of config files in {ConfigFolder}...", configFolder);
        
        // Get both JSON and YAML files
        var jsonFiles = Directory.GetFiles(configFolder, "*.json");
        var yamlFiles = Directory.GetFiles(configFolder, "*.yaml");
        var ymlFiles = Directory.GetFiles(configFolder, "*.yml");
        var allConfigFiles = jsonFiles.Concat(yamlFiles).Concat(ymlFiles).ToArray();
        
        if (allConfigFiles.Length == 0)
        {
            _logger.LogDebug("No config files found");
            return configInfo;
        }

        foreach (var configFile in allConfigFiles)
        {
            _logger.LogDebug("Reading file {ConfigFile}...", configFile);

            var fileContents = File.ReadAllText(configFile);
            
            // Check for plugins marker (case-insensitive)
            // For JSON: "plugins":
            // For YAML: plugins:
            if (fileContents.Contains("\"plugins\":", StringComparison.OrdinalIgnoreCase) ||
                fileContents.Contains("plugins:", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogDebug("File {ConfigFile} contains proxy config", configFile);
                configInfo.ConfigFiles.Add(configFile);
                continue;
            }

            // Check for responses/mocks marker (case-insensitive)
            // For JSON: "responses": or "mocks":
            // For YAML: responses: or mocks:
            if (fileContents.Contains("\"responses\":", StringComparison.OrdinalIgnoreCase) ||
                fileContents.Contains("\"mocks\":", StringComparison.OrdinalIgnoreCase) ||
                fileContents.Contains("responses:", StringComparison.OrdinalIgnoreCase) ||
                fileContents.Contains("mocks:", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogDebug("File {ConfigFile} contains mock data", configFile);
                configInfo.MockFiles.Add(configFile);
                continue;
            }

            _logger.LogDebug("File {ConfigFile} is not a proxy config or mock data", configFile);
        }

        if (configInfo.ConfigFiles.Any())
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug("Found {ConfigFilesCount} proxy config files. Clearing mocks...", configInfo.ConfigFiles.Count);
            }
            configInfo.MockFiles.Clear();
        }

        return configInfo;
    }

    private async Task<string[]> GetFilesToDownloadAsync(string sampleFolderName)
    {
        _logger.LogDebug("Getting list of files in Dev Proxy samples repo...");
        var url = new Uri($"https://api.github.com/repos/pnp/proxy-samples/git/trees/main?recursive=1");
        var response = await _httpClient.GetAsync(url);

        if (response.IsSuccessStatusCode)
        {
            var content = await response.Content.ReadAsStringAsync();
            var tree = JsonSerializer.Deserialize<GitHubTreeResponse>(content, ProxyUtils.JsonSerializerOptions) ??
                throw new HttpRequestException("Failed to get list of files from GitHub");
            var samplePath = $"samples/{sampleFolderName}";

            var filesToDownload = tree.Tree
                .Where(f => f.Path.StartsWith(samplePath, StringComparison.OrdinalIgnoreCase) && f.Type == "blob")
                .Select(f => f.Path)
                .ToArray();

            foreach (var file in filesToDownload)
            {
                _logger.LogDebug("Found file {File}", file);
            }

            return filesToDownload;
        }
        else
        {
            throw new HttpRequestException($"Failed to get list of files from GitHub. Status code: {response.StatusCode}");
        }
    }

    private async Task DownloadFileAsync(string filePath, string targetFolderPath, string configId)
    {
        var url = new Uri($"https://raw.githubusercontent.com/pnp/proxy-samples/main/{filePath.Replace("#", "%23", StringComparison.OrdinalIgnoreCase)}");
        _logger.LogDebug("Downloading file {FilePath}...", filePath);

        var response = await _httpClient.GetAsync(url);

        if (response.IsSuccessStatusCode)
        {
            var contentStream = await response.Content.ReadAsStreamAsync();
            var filePathInsideSample = Path.GetRelativePath($"samples/{configId}", filePath);
            var directoryNameInsideSample = Path.GetDirectoryName(filePathInsideSample);
            if (directoryNameInsideSample is not null)
            {
                _ = Directory.CreateDirectory(Path.Combine(targetFolderPath, directoryNameInsideSample));
            }
            var localFilePath = Path.Combine(targetFolderPath, filePathInsideSample);

            using var fileStream = new FileStream(localFilePath, FileMode.Create, FileAccess.Write, FileShare.None);
            await contentStream.CopyToAsync(fileStream);

            _logger.LogDebug("File downloaded successfully to {LocalFilePath}", localFilePath);
        }
        else
        {
            throw new HttpRequestException($"Failed to download file {url}. Status code: {response.StatusCode}");
        }
    }

    private async Task CreateConfigFileAsync(string name, OutputFormat outputFormat)
    {
        try
        {
            var snippets = await DownloadSnippetsAsync();
            if (snippets is null)
            {
                return;
            }

            if (!snippets.TryGetValue(configFileSnippetName, out var snippet))
            {
                if (outputFormat == OutputFormat.Text)
                {
                    _logger.LogError("Snippet {SnippetName} not found", configFileSnippetName);
                }
                return;
            }

            if (snippet.Body is null || snippet.Body.Length == 0)
            {
                if (outputFormat == OutputFormat.Text)
                {
                    _logger.LogError("Snippet {SnippetName} is empty", configFileSnippetName);
                }
                return;
            }

            var snippetBody = GetSnippetBody(snippet.Body);
            var targetFileName = GetTargetFileName(name);
            await File.WriteAllTextAsync(targetFileName, snippetBody);

            if (outputFormat == OutputFormat.Json)
            {
                var json = JsonSerializer.Serialize(new
                {
                    path = targetFileName
                }, ProxyUtils.JsonSerializerOptions);
                _logger.LogStructuredOutput(json);
            }
            else
            {
                _logger.LogInformation("Config file created at {TargetFileName}", targetFileName);
            }
        }
        catch (Exception ex)
        {
            if (outputFormat == OutputFormat.Text)
            {
                _logger.LogError(ex, "Error downloading config");
            }
        }
    }

    private async Task<Dictionary<string, VisualStudioCodeSnippet>?> DownloadSnippetsAsync()
    {
        _logger.LogDebug("Downloading snippets from {SnippetsFileUrl}...", snippetsFileUrl);
        var response = await _httpClient.GetAsync(new Uri(snippetsFileUrl));
        if (response.IsSuccessStatusCode)
        {
            var content = await response.Content.ReadAsStringAsync();
            try
            {
                return JsonSerializer.Deserialize<Dictionary<string, VisualStudioCodeSnippet>>(content, ProxyUtils.JsonSerializerOptions);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to parse snippets from {Url}", snippetsFileUrl);
                return null;
            }
        }
        else
        {
            _logger.LogError("Failed to download snippets. Status code: {StatusCode}", response.StatusCode);
            return null;
        }
    }

    private string GetTargetFileName(string name)
    {
        var originalNameWithoutExtension = Path.GetFileNameWithoutExtension(name);
        var originalExtension = Path.GetExtension(name);
        var counter = 1;

        while (true)
        {
            if (!File.Exists(name))
            {
                return name;
            }

            var newName = $"{originalNameWithoutExtension}-{++counter}{originalExtension}";
            _logger.LogDebug("File {Name} already exists. Trying {NewName}...", name, newName);
            name = newName;
        }
    }

    private static string GetTargetFolderPath(string appFolder, string configId)
    {
        var baseFolder = Path.Combine(appFolder, "config", configId);
        var newFolder = baseFolder;
        var i = 1;
        while (Directory.Exists(newFolder))
        {
            newFolder = baseFolder + i++;
        }

        return newFolder;
    }

    private static string? GetSnippetBody(string[] bodyLines)
    {
        var body = string.Join("\n", bodyLines);
        // unescape $
        body = body.Replace("\\$", "$", StringComparison.OrdinalIgnoreCase);
        // remove snippet $n markers
        body = Regex.Replace(body, @"\$[0-9]+", "");
        return body;
    }

    private static string? ResolveConfigFile(string? configFilePath)
    {
        if (!string.IsNullOrEmpty(configFilePath))
        {
            var resolved = Path.GetFullPath(ProxyUtils.ReplacePathTokens(configFilePath));
            return File.Exists(resolved) ? resolved : null;
        }

        foreach (var configFile in ProxyUtils.GetConfigFileCandidates(null))
        {
            if (!string.IsNullOrEmpty(configFile) && File.Exists(configFile))
            {
                return Path.GetFullPath(configFile);
            }
        }

        return null;
    }

    private static async Task<int> ValidateConfigCoreAsync(
        string? configFilePath,
        bool isJsonOutput,
        HttpClient httpClient,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var errors = new List<ValidationMessage>();
        var warnings = new List<ValidationMessage>();
        var pluginNames = new List<string>();
        var urlPatterns = new List<string>();

        var resolvedConfigFile = ResolveConfigFile(configFilePath);
        if (resolvedConfigFile is null)
        {
            errors.Add(new("configFile", configFilePath is not null
                ? $"Configuration file '{configFilePath}' not found"
                : "No configuration file found"));
            WriteResults(isJsonOutput, null, errors, warnings, pluginNames, urlPatterns, logger);
            return 2;
        }

        string configJson;
        JsonDocument configDoc;
        try
        {
            var configText = await File.ReadAllTextAsync(resolvedConfigFile, cancellationToken);

            if (ProxyYaml.IsYamlFile(resolvedConfigFile))
            {
                if (!ProxyYaml.TryConvertYamlToJson(configText, out var converted, out var yamlError))
                {
                    throw new JsonException($"Could not convert YAML configuration to JSON: {yamlError}");
                }
                configJson = converted!;
            }
            else
            {
                configJson = configText;
            }

            configDoc = JsonDocument.Parse(configJson, ProxyUtils.JsonDocumentOptions);
        }
        catch (JsonException ex)
        {
            errors.Add(new("configFile", $"Invalid configuration: {ex.Message}"));
            WriteResults(isJsonOutput, resolvedConfigFile, errors, warnings, pluginNames, urlPatterns, logger);
            return 2;
        }
        catch (YamlException ex)
        {
            errors.Add(new("configFile", $"Invalid YAML: {ex.Message}"));
            WriteResults(isJsonOutput, resolvedConfigFile, errors, warnings, pluginNames, urlPatterns, logger);
            return 2;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            errors.Add(new("configFile", $"Could not read configuration file: {ex.Message}"));
            WriteResults(isJsonOutput, resolvedConfigFile, errors, warnings, pluginNames, urlPatterns, logger);
            return 2;
        }

        using (configDoc)
        {
            var schemaUrl = configDoc.RootElement.TryGetProperty("$schema", out var schemaProp)
                ? schemaProp.GetString()
                : null;
            if (!string.IsNullOrEmpty(schemaUrl))
            {
                try
                {
                    var (isValid, validationErrors) = await ProxyUtils.ValidateJsonAsync(
                        configJson, schemaUrl, httpClient, logger, cancellationToken);
                    if (!isValid)
                    {
                        foreach (var error in validationErrors)
                        {
                            errors.Add(new("schema", error));
                        }
                    }
                }
                catch (Exception ex)
                {
                    warnings.Add(new("$schema", $"Could not validate schema: {ex.Message}"));
                }

                ValidateSchemaVersion(schemaUrl, warnings);
            }
            else
            {
                warnings.Add(new("$schema", "No schema URL found, skipping schema validation"));
            }

            var configFileDirectory = Path.GetDirectoryName(resolvedConfigFile) ?? ".";

            if (configDoc.RootElement.TryGetProperty("plugins", out var pluginsElement) &&
                pluginsElement.ValueKind == JsonValueKind.Array)
            {
                ValidatePlugins(pluginsElement, configFileDirectory, errors, warnings, pluginNames);
            }
            else
            {
                errors.Add(new("plugins", "No plugins configured"));
            }

            if (configDoc.RootElement.TryGetProperty("urlsToWatch", out var urlsElement) &&
                urlsElement.ValueKind == JsonValueKind.Array)
            {
                ValidateUrls(urlsElement, errors, warnings, urlPatterns);
            }
            else
            {
                warnings.Add(new("urlsToWatch", "No URLs to watch configured"));
            }
        }

        var isConfigValid = errors.Count == 0;
        WriteResults(isJsonOutput, resolvedConfigFile, errors, warnings, pluginNames, urlPatterns, logger);
        return isConfigValid ? 0 : 2;
    }

    private static void ValidateSchemaVersion(string schemaUrl, List<ValidationMessage> warnings)
    {
        var warning = ProxyUtils.GetSchemaVersionMismatchWarning(schemaUrl);
        if (warning is not null)
        {
            warnings.Add(new("$schema", warning));
        }
    }

    private static void ValidatePlugins(
        JsonElement pluginsElement,
        string configFileDirectory,
        List<ValidationMessage> errors,
        List<ValidationMessage> warnings,
        List<string> pluginNames)
    {
        var hasEnabledPlugins = false;
        var i = 0;

        foreach (var plugin in pluginsElement.EnumerateArray())
        {
            string? name;
            bool enabled;
            string? pluginPath;
            try
            {
                name = plugin.TryGetProperty("name", out var nameProp) ? nameProp.GetString() : null;
                enabled = !plugin.TryGetProperty("enabled", out var enabledProp) || enabledProp.GetBoolean();
                pluginPath = plugin.TryGetProperty("pluginPath", out var pathProp) ? pathProp.GetString() : null;
            }
            catch (InvalidOperationException ex)
            {
                errors.Add(new($"plugins[{i}]", $"Invalid plugin definition: {ex.Message}"));
                i++;
                continue;
            }

            if (string.IsNullOrEmpty(name))
            {
                errors.Add(new($"plugins[{i}].name", "Plugin name is required"));
                i++;
                continue;
            }

            if (!enabled)
            {
                i++;
                continue;
            }

            hasEnabledPlugins = true;
            pluginNames.Add(name);

            if (string.IsNullOrEmpty(pluginPath))
            {
                errors.Add(new($"plugins[{i}].pluginPath", $"Plugin path is required for '{name}'"));
                i++;
                continue;
            }

            var resolvedPluginPath = Path.GetFullPath(
                Path.Combine(
                    configFileDirectory,
                    ProxyUtils.ReplacePathTokens(pluginPath.Replace('\\', Path.DirectorySeparatorChar))));

            if (!File.Exists(resolvedPluginPath))
            {
                errors.Add(new($"plugins[{i}].pluginPath",
                    $"Plugin assembly '{resolvedPluginPath}' not found"));
                i++;
                continue;
            }

            try
            {
                var pluginLoadContext = new PluginLoadContext(resolvedPluginPath);
                var assembly = pluginLoadContext.LoadFromAssemblyName(
                    new AssemblyName(Path.GetFileNameWithoutExtension(resolvedPluginPath)));
                var pluginType = assembly.GetTypes()
                    .FirstOrDefault(t => t.Name == name && typeof(IPlugin).IsAssignableFrom(t));
                if (pluginType is null)
                {
                    errors.Add(new($"plugins[{i}].name",
                        $"Plugin '{name}' not found in assembly '{Path.GetFileName(resolvedPluginPath)}'"));
                }
            }
            catch (Exception ex)
            {
                errors.Add(new($"plugins[{i}]",
                    $"Failed to load plugin assembly: {ex.Message}"));
            }

            i++;
        }

        if (!hasEnabledPlugins)
        {
            errors.Add(new("plugins", "No enabled plugins found"));
        }
    }

    private static void ValidateUrls(
        JsonElement urlsElement,
        List<ValidationMessage> errors,
        List<ValidationMessage> warnings,
        List<string> urlPatterns)
    {
        var i = 0;
        foreach (var url in urlsElement.EnumerateArray())
        {
            if (url.ValueKind != JsonValueKind.String)
            {
                errors.Add(new($"urlsToWatch[{i}]", $"Expected a string but got {url.ValueKind}"));
                i++;
                continue;
            }

            var pattern = url.GetString();
            if (string.IsNullOrEmpty(pattern))
            {
                warnings.Add(new($"urlsToWatch[{i}]", "Empty URL pattern"));
            }
            else
            {
                urlPatterns.Add(pattern);
                try
                {
                    var cleanPattern = pattern.StartsWith('!') ? pattern[1..] : pattern;
                    _ = new Regex(
                        $"^{Regex.Escape(cleanPattern).Replace("\\*", ".*", StringComparison.OrdinalIgnoreCase)}$");
                }
                catch (ArgumentException ex)
                {
                    errors.Add(new($"urlsToWatch[{i}]",
                        $"Invalid URL pattern '{pattern}': {ex.Message}"));
                }
            }
            i++;
        }
    }

    private static void WriteResults(
        bool isJsonOutput,
        string? configFile,
        List<ValidationMessage> errors,
        List<ValidationMessage> warnings,
        List<string> pluginNames,
        List<string> urlPatterns,
        ILogger logger)
    {
        var isValid = errors.Count == 0;

        if (isJsonOutput)
        {
            WriteJsonResults(configFile, errors, warnings, pluginNames, urlPatterns, isValid, logger);
        }
        else
        {
            WriteTextResults(configFile, errors, warnings, pluginNames, urlPatterns, isValid, logger);
        }
    }

    private static void WriteJsonResults(
        string? configFile,
        List<ValidationMessage> errors,
        List<ValidationMessage> warnings,
        List<string> pluginNames,
        List<string> urlPatterns,
        bool isValid,
        ILogger logger)
    {
        var result = new
        {
            valid = isValid,
            configFile,
            plugins = pluginNames,
            urlsToWatch = urlPatterns,
            errors = errors.Select(e => new { path = e.Path, message = e.Message }),
            warnings = warnings.Select(w => new { path = w.Path, message = w.Message })
        };

        var json = JsonSerializer.Serialize(result, ProxyUtils.JsonSerializerOptions);
        logger.LogInformation("{Result}", json);
    }

    private static void WriteTextResults(
        string? configFile,
        List<ValidationMessage> errors,
        List<ValidationMessage> warnings,
        List<string> pluginNames,
        List<string> urlPatterns,
        bool isValid,
        ILogger logger)
    {
        if (isValid)
        {
            logger.LogInformation("\u2713 Configuration is valid");
        }
        else
        {
            logger.LogError("\u2717 Configuration is invalid");
        }

        if (configFile is not null)
        {
            logger.LogInformation("  Config file: {ConfigFile}", configFile);
        }
        if (pluginNames.Count > 0)
        {
            logger.LogInformation("  Plugins: {Count} loaded", pluginNames.Count);
        }
        if (urlPatterns.Count > 0)
        {
            logger.LogInformation("  URLs to watch: {Count} pattern{Plural}", urlPatterns.Count, urlPatterns.Count != 1 ? "s" : "");
        }

        if (errors.Count > 0)
        {
            logger.LogInformation("");
            foreach (var error in errors)
            {
                logger.LogError("  - {Path}: {Message}", error.Path, error.Message);
            }
        }

        if (warnings.Count > 0)
        {
            logger.LogInformation("");
            foreach (var warning in warnings)
            {
                logger.LogWarning("  - {Path}: {Message}", warning.Path, warning.Message);
            }
        }
    }

    private sealed record ValidationMessage(string Path, string Message);
}