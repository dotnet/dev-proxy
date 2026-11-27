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
using System.Globalization;
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

sealed class ConfigCommand : Command
{
    private enum ConfigFileFormat
    {
        Json,
        Yaml
    }

    private readonly ILogger _logger;
    private readonly IProxyConfiguration _proxyConfiguration;
    private readonly HttpClient _httpClient;
    private const string ConfigTemplatesFolder = "config-templates";
    private static readonly string[] configFilePatterns = ["*.json", "*.yaml", "*.yml"];

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
        var parseResult = ParseValidateArgs(args, out var configFileOptions, out var outputOption);

        OutputFormat? output = null;
        try
        {
            output = parseResult.GetValue(outputOption);
        }
        catch (InvalidOperationException)
        {
            // invalid --output value, reported as a parse error below
        }
        var isJsonOutput = output == OutputFormat.Json;
        var configFile = parseResult.Errors.Count == 0
            ? configFileOptions
                .Select(o => parseResult.GetValue(o))
                .FirstOrDefault(v => v is not null)
            : null;

        using var httpClient = new HttpClient();
        httpClient.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue("dev-proxy", ProxyUtils.ProductVersion));

        var formatterName = isJsonOutput
            ? JsonConsoleFormatter.FormatterName
            : ProxyConsoleFormatter.DefaultCategoryName;

        using var loggerFactory = LoggerFactory.Create(builder =>
        {
            _ = builder
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

        if (parseResult.Errors.Count > 0)
        {
            foreach (var error in parseResult.Errors)
            {
                logger.LogError("{Error}", error.Message);
            }
            return 2;
        }

        return await ValidateConfigCoreAsync(configFile, isJsonOutput, httpClient, logger, CancellationToken.None);
    }

    /// <summary>
    /// Parses 'validate' and 'config validate' arguments with a lightweight
    /// command tree that doesn't require loading the config or plugins.
    /// </summary>
    internal static ParseResult ParseValidateArgs(
        string[] args,
        out IReadOnlyList<Option<string?>> configFileOptions,
        out Option<OutputFormat?> outputOption)
    {
        outputOption = new Option<OutputFormat?>(DevProxyCommand.OutputOptionName) { Recursive = true };
        var validateConfigFileOption = CreateValidateConfigFileOption();
        var configValidateConfigFileOption = CreateValidateConfigFileOption();
        configFileOptions = [validateConfigFileOption, configValidateConfigFileOption];

        var rootCommand = new RootCommand
        {
            new Command("validate") { validateConfigFileOption },
            new Command("config") { new Command("validate") { configValidateConfigFileOption } }
        };
        rootCommand.Options.Add(outputOption);
        // accepted for consistency with the main command, but not used by validation
        rootCommand.Options.Add(new Option<string?>(DevProxyCommand.LogLevelOptionName) { Recursive = true });
        rootCommand.Options.Add(new Option<bool>(DevProxyCommand.NoColorOptionName) { Arity = ArgumentArity.Zero, Recursive = true });

        return rootCommand.Parse(args);
    }

    internal static Option<string?> CreateValidateConfigFileOption() => new("--config-file", "-c")
    {
        Description = "The path to the configuration file to validate",
        HelpName = "config-file"
    };

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
            return configId is null ? 1 : await DownloadConfigAsync(configId, outputFormat);
        });

        var configNewCommand = new Command("new", "Create new Dev Proxy configuration file");
        var nameArgument = new Argument<string>("name")
        {
            Arity = ArgumentArity.ZeroOrOne,
            Description = "Name of the configuration file. Defaults to devproxyrc.json (or devproxyrc.yaml with --format yaml)."
        };
        var formatOption = new Option<ConfigFileFormat?>("--format")
        {
            Description = "Configuration format to use (json or yaml)"
        };
        configNewCommand.Add(nameArgument);
        configNewCommand.Add(formatOption);
        configNewCommand.SetAction(async (parseResult) =>
        {
            var format = parseResult.GetValue(formatOption);
            var name = parseResult.GetValue(nameArgument) ?? GetDefaultConfigFileName(format);
            var outputFormat = parseResult.GetValueOrDefault<OutputFormat?>(DevProxyCommand.OutputOptionName) ?? OutputFormat.Text;
            return await CreateConfigFileAsync(name, format, outputFormat);
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

        var configValidateCommand = new Command("validate", "Validate a Dev Proxy configuration file without starting the proxy");
        var validateConfigFileOption = CreateValidateConfigFileOption();
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
            ("devproxy config new", "Create default devproxyrc.json"),
            ("devproxy config new --format yaml", "Create default devproxyrc.yaml"),
            ("devproxy config new myconfig.json", "Create named config file"),
            ("devproxy config get <config-id>", "Download config from gallery"),
            ("devproxy config open", "Open config in default editor"),
            ("devproxy config validate -c myconfig.json", "Validate config without starting"),
        ]);
    }

    private async Task<int> DownloadConfigAsync(string configId, OutputFormat outputFormat)
    {
        try
        {
            var dataFolder = ProxyUtils.DataFolder;

            var configFolderPath = Path.Combine(dataFolder, "configs");
            _logger.LogDebug("Checking if config folder {ConfigFolderPath} exists...", configFolderPath);
            if (!Directory.Exists(configFolderPath))
            {
                _logger.LogDebug("Config folder not found, creating it...");
                _ = Directory.CreateDirectory(configFolderPath);
                _logger.LogDebug("Config folder created");
            }

            _logger.LogDebug("Getting target folder path for config {ConfigId}...", configId);
            var targetFolderPath = GetTargetFolderPath(dataFolder, configId);
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
                return 1;
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
                return 0;
            }

            _logger.LogInformation("Config saved in {TargetFolderPath}\r\n", targetFolderPath);

            if (configInfo.ConfigFiles.Count == 0 && configInfo.MockFiles.Count == 0)
            {
                return 0;
            }

            if (configInfo.ConfigFiles.Count > 0)
            {
                _logger.LogInformation("To start Dev Proxy with the config, run:");
                foreach (var configFile in configInfo.ConfigFiles)
                {
                    if (_logger.IsEnabled(LogLevel.Information))
                    {
                        _logger.LogInformation("  devproxy --config-file \"{ConfigFile}\"", configFile.Replace(dataFolder, "~dataFolder", StringComparison.OrdinalIgnoreCase));
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
                        _logger.LogInformation("  devproxy --mock-file \"{MockFile}\"", mockFile.Replace(dataFolder, "~dataFolder", StringComparison.OrdinalIgnoreCase));
                    }
                }
            }

            return 0;
        }
        catch (Exception ex)
        {
            if (outputFormat == OutputFormat.Text)
            {
                _logger.LogError(ex, "Error downloading config");
            }
            return 1;
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
    /// Files in the .devproxy subfolder are listed before files in the
    /// root folder.
    /// </remarks>
    /// <param name="configFolder">Full path to the folder with config files</param>
    /// <returns>Array of files that can be used to start proxy with</returns>
    private ProxyConfigInfo GetConfigInfo(string configFolder)
    {
        var configInfo = new ProxyConfigInfo();

        _logger.LogDebug("Getting list of config files in {ConfigFolder}...", configFolder);

        // Presets in the samples gallery keep their config in a .devproxy
        // subfolder, so scan it first, followed by the root folder
        var foldersToScan = new[] { Path.Combine(configFolder, ".devproxy"), configFolder }
            .Where(Directory.Exists);
        var allConfigFiles = foldersToScan
            .SelectMany(folder => configFilePatterns
                .SelectMany(pattern => Directory.GetFiles(folder, pattern)))
            .ToArray();

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

        if (configInfo.ConfigFiles.Count > 0)
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

    private async Task<int> CreateConfigFileAsync(string name, ConfigFileFormat? format, OutputFormat outputFormat)
    {
        try
        {
            var selectedFormat = format ?? GetConfigFileFormatFromFileName(name);
            var templateFileName = selectedFormat == ConfigFileFormat.Yaml ? "devproxyrc.yaml" : "devproxyrc.json";
            var templateFilePath = Path.Combine(AppContext.BaseDirectory, ConfigTemplatesFolder, templateFileName);
            _logger.LogDebug("Reading config template from {TemplateFilePath}...", templateFilePath);
            var template = await File.ReadAllTextAsync(templateFilePath);

            var targetFileName = GetTargetFileName(name);
            await File.WriteAllTextAsync(targetFileName, template);

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
            return 0;
        }
        catch (Exception ex)
        {
            if (outputFormat == OutputFormat.Text)
            {
                _logger.LogError(ex, "Error creating config");
            }
            return 1;
        }
    }

    private static string GetDefaultConfigFileName(ConfigFileFormat? format) =>
        format == ConfigFileFormat.Yaml ? "devproxyrc.yaml" : "devproxyrc.json";

    private static ConfigFileFormat GetConfigFileFormatFromFileName(string name)
    {
        var extension = Path.GetExtension(name).ToLowerInvariant();
        return extension is ".yaml" or ".yml" ? ConfigFileFormat.Yaml : ConfigFileFormat.Json;
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

    private static string GetTargetFolderPath(string dataFolder, string configId)
    {
        var baseFolder = Path.Combine(dataFolder, "configs", configId);
        var newFolder = baseFolder;
        var i = 1;
        while (Directory.Exists(newFolder))
        {
            newFolder = baseFolder + i++;
        }

        return newFolder;
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

    internal static async Task<int> ValidateConfigCoreAsync(
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
            if (configDoc.RootElement.ValueKind != JsonValueKind.Object)
            {
                errors.Add(new("configFile", $"Expected the configuration to be an object but got {configDoc.RootElement.ValueKind}"));
                WriteResults(isJsonOutput, resolvedConfigFile, errors, warnings, pluginNames, urlPatterns, logger);
                return 2;
            }

            // The configuration with resolved environment placeholders, which
            // is what the proxy uses at startup. Null if it can't be loaded.
            IConfiguration? configuration = null;
            try
            {
                configuration = new ConfigurationBuilder().AddConfigFile(resolvedConfigFile).Build();
            }
            catch (Exception ex) when (ex is FormatException or InvalidDataException or YamlException)
            {
                errors.Add(new("configFile", $"Invalid configuration: {ex.Message}"));
            }

            string? schemaUrl = null;
            if (TryGetPropertyIgnoreCase(configDoc.RootElement, "$schema", out var schemaProp))
            {
                if (schemaProp.ValueKind == JsonValueKind.String)
                {
                    schemaUrl = configuration?["$schema"] ?? schemaProp.GetString();
                }
                else if (schemaProp.ValueKind != JsonValueKind.Null)
                {
                    errors.Add(new("$schema", $"Expected a string but got {schemaProp.ValueKind}"));
                }
            }

            if (!string.IsNullOrWhiteSpace(schemaUrl))
            {
                var result = await ProxyUtils.ValidateJsonAgainstSchemaAsync(
                    configJson, schemaUrl, httpClient, logger, cancellationToken);
                if (result.Status == SchemaValidationStatus.SchemaUnavailable)
                {
                    warnings.Add(new("$schema", $"Could not retrieve schema, skipping schema validation: {string.Join(", ", result.Errors)}"));
                }
                else if (result.Status == SchemaValidationStatus.Invalid)
                {
                    foreach (var error in result.Errors)
                    {
                        errors.Add(new("schema", error));
                    }
                }

                ValidateSchemaVersion(schemaUrl, warnings);
            }
            else if (schemaProp.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null or JsonValueKind.String)
            {
                warnings.Add(new("$schema", "No schema URL found, skipping schema validation"));
            }

            var configFileDirectory = Path.GetDirectoryName(resolvedConfigFile) ?? ".";

            List<PluginEntry>? pluginEntries = null;
            if (TryGetPropertyIgnoreCase(configDoc.RootElement, "plugins", out var pluginsElement) &&
                pluginsElement.ValueKind == JsonValueKind.Array)
            {
                pluginEntries = GetPluginEntries(pluginsElement, configuration);
                ValidatePlugins(pluginEntries, configFileDirectory, errors, pluginNames);
            }
            else
            {
                errors.Add(new("plugins", "No plugins configured"));
            }

            if (TryGetPropertyIgnoreCase(configDoc.RootElement, "urlsToWatch", out var urlsElement) &&
                urlsElement.ValueKind == JsonValueKind.Array)
            {
                ValidateUrls(urlsElement, errors, warnings, urlPatterns);
            }
            else
            {
                warnings.Add(new("urlsToWatch", "No URLs to watch configured"));
            }

            if (pluginEntries is not null)
            {
                await ValidatePluginSettingsAsync(
                    configDoc.RootElement, configuration, pluginEntries, urlPatterns.Count > 0, httpClient, logger, errors, warnings, cancellationToken);
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

    /// <summary>
    /// Reads plugin references the way the proxy does at startup: from the
    /// loaded configuration with environment placeholders resolved. Falls
    /// back to the raw JSON if the configuration couldn't be loaded.
    /// </summary>
    private static List<PluginEntry> GetPluginEntries(JsonElement pluginsElement, IConfiguration? configuration)
    {
        var entries = new List<PluginEntry>();
        var i = 0;

        foreach (var plugin in pluginsElement.EnumerateArray())
        {
            if (configuration is not null)
            {
                var pluginSection = configuration.GetSection($"plugins:{i}");
                var enabledValue = pluginSection["enabled"];
                var enabled = true;
                string? error = null;
                if (enabledValue is not null && !bool.TryParse(enabledValue, out enabled))
                {
                    error = $"Invalid plugin definition: '{enabledValue}' is not a valid value for 'enabled'";
                }
                entries.Add(new(i, plugin, pluginSection["name"], enabled, pluginSection["pluginPath"], pluginSection["configSection"], error));
            }
            else
            {
                try
                {
                    var name = TryGetPropertyIgnoreCase(plugin, "name", out var nameProp) ? nameProp.GetString() : null;
                    var enabled = !TryGetPropertyIgnoreCase(plugin, "enabled", out var enabledProp) || enabledProp.GetBoolean();
                    var pluginPath = TryGetPropertyIgnoreCase(plugin, "pluginPath", out var pathProp) ? pathProp.GetString() : null;
                    var configSection = TryGetPropertyIgnoreCase(plugin, "configSection", out var sectionProp) &&
                        sectionProp.ValueKind == JsonValueKind.String ? sectionProp.GetString() : null;
                    entries.Add(new(i, plugin, name, enabled, pluginPath, configSection, null));
                }
                catch (InvalidOperationException ex)
                {
                    entries.Add(new(i, plugin, null, false, null, null, $"Invalid plugin definition: {ex.Message}"));
                }
            }
            i++;
        }

        return entries;
    }

    private static void ValidatePlugins(
        List<PluginEntry> pluginEntries,
        string configFileDirectory,
        List<ValidationMessage> errors,
        List<string> pluginNames)
    {
        var hasEnabledPlugins = false;

        foreach (var (i, _, name, enabled, pluginPath, _, error) in pluginEntries)
        {
            if (error is not null)
            {
                errors.Add(new($"plugins[{i}]", error));
                continue;
            }

            if (string.IsNullOrEmpty(name))
            {
                errors.Add(new($"plugins[{i}].name", "Plugin name is required"));
                continue;
            }

            if (!enabled)
            {
                continue;
            }

            hasEnabledPlugins = true;
            pluginNames.Add(name);

            if (string.IsNullOrEmpty(pluginPath))
            {
                errors.Add(new($"plugins[{i}].pluginPath", $"Plugin path is required for '{name}'"));
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
        }

        if (!hasEnabledPlugins)
        {
            errors.Add(new("plugins", "No enabled plugins found"));
        }
    }

    /// <summary>
    /// Validates plugin-level settings that are otherwise only checked when
    /// the proxy starts: each plugin needs URLs to watch, and plugin config
    /// sections must exist and match the plugin's schema.
    /// </summary>
    private static async Task ValidatePluginSettingsAsync(
        JsonElement rootElement,
        IConfiguration? configuration,
        List<PluginEntry> pluginEntries,
        bool hasGlobalUrls,
        HttpClient httpClient,
        ILogger logger,
        List<ValidationMessage> errors,
        List<ValidationMessage> warnings,
        CancellationToken cancellationToken)
    {
        var validateSchemas = configuration is not null
            ? !(bool.TryParse(configuration["validateSchemas"], out var validateSchemasValue) && !validateSchemasValue)
            : !TryGetPropertyIgnoreCase(rootElement, "validateSchemas", out var validateSchemasProp) || !IsFalse(validateSchemasProp);

        foreach (var (i, plugin, name, enabled, _, configSectionName, pluginError) in pluginEntries)
        {
            if (pluginError is not null || string.IsNullOrEmpty(name) || !enabled)
            {
                continue;
            }

            var hasPluginUrls = TryGetPropertyIgnoreCase(plugin, "urlsToWatch", out var pluginUrlsProp) &&
                pluginUrlsProp.ValueKind == JsonValueKind.Array &&
                pluginUrlsProp.GetArrayLength() > 0;
            if (!hasPluginUrls && !hasGlobalUrls)
            {
                warnings.Add(new($"plugins[{i}].urlsToWatch",
                    $"Plugin '{name}' has no URLs to watch and won't be loaded. Add URLs to the global or plugin-level urlsToWatch, or pass --urls-to-watch when starting Dev Proxy"));
            }

            if (string.IsNullOrEmpty(configSectionName))
            {
                continue;
            }

            var hasRawSection = TryGetConfigSection(rootElement, configSectionName.Split(':'), 0, out var configSection);
            if (!hasRawSection && configuration?.GetSection(configSectionName).Exists() != true)
            {
                warnings.Add(new($"plugins[{i}].configSection",
                    $"Config section '{configSectionName}' for plugin '{name}' not found. The plugin will use its default configuration"));
                continue;
            }

            if (!validateSchemas || !hasRawSection)
            {
                continue;
            }

            string? rawSectionSchemaUrl = null;
            if (TryGetPropertyIgnoreCase(configSection, "$schema", out var sectionSchemaProp) &&
                sectionSchemaProp.ValueKind != JsonValueKind.Null)
            {
                if (sectionSchemaProp.ValueKind != JsonValueKind.String)
                {
                    errors.Add(new($"{configSectionName}.$schema", $"Expected a string but got {sectionSchemaProp.ValueKind}"));
                    continue;
                }
                rawSectionSchemaUrl = sectionSchemaProp.GetString();
            }

            // the schema URL can also come from a root key like "mySection:$schema",
            // which startup reads through IConfiguration
            var sectionSchemaUrl = configuration?.GetSection(configSectionName)["$schema"] ?? rawSectionSchemaUrl;
            if (string.IsNullOrWhiteSpace(sectionSchemaUrl))
            {
                continue;
            }
            try
            {
                var result = await ProxyUtils.ValidateJsonAgainstSchemaAsync(
                    configSection.GetRawText(), sectionSchemaUrl, httpClient, logger, cancellationToken);
                if (result.Status == SchemaValidationStatus.SchemaUnavailable)
                {
                    warnings.Add(new($"{configSectionName}.$schema", $"Could not retrieve schema, skipping schema validation: {string.Join(", ", result.Errors)}"));
                }
                else if (result.Status == SchemaValidationStatus.Invalid)
                {
                    foreach (var error in result.Errors)
                    {
                        errors.Add(new(configSectionName, error));
                    }
                }
            }
            catch (Exception ex)
            {
                warnings.Add(new($"{configSectionName}.$schema", $"Could not validate schema: {ex.Message}"));
            }

            var versionWarning = ProxyUtils.GetSchemaVersionMismatchWarning(sectionSchemaUrl);
            if (versionWarning is not null)
            {
                warnings.Add(new($"{configSectionName}.$schema", versionWarning));
            }
        }
    }

    /// <summary>
    /// Resolves a config section using the same semantics as IConfiguration:
    /// case-insensitive keys, ':' as the path separator (including literal
    /// keys that contain ':') and numeric array indices.
    /// </summary>
    private static bool TryGetConfigSection(JsonElement element, string[] segments, int start, out JsonElement section)
    {
        if (start == segments.Length)
        {
            section = element;
            return true;
        }

        for (var end = segments.Length; end > start; end--)
        {
            var key = string.Join(':', segments, start, end - start);
            if (TryGetPropertyIgnoreCase(element, key, out var child) &&
                TryGetConfigSection(child, segments, end, out section))
            {
                return true;
            }
        }

        if (element.ValueKind == JsonValueKind.Array &&
            int.TryParse(segments[start], NumberStyles.None, CultureInfo.InvariantCulture, out var index) &&
            index < element.GetArrayLength() &&
            TryGetConfigSection(element[index], segments, start + 1, out section))
        {
            return true;
        }

        section = default;
        return false;
    }

    /// <summary>
    /// Gets a property case-insensitively, like IConfiguration does. When keys
    /// differ only by case, the last one wins.
    /// </summary>
    private static bool TryGetPropertyIgnoreCase(JsonElement element, string name, out JsonElement value)
    {
        value = default;
        if (element.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var found = false;
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                found = true;
            }
        }
        return found;
    }

    private static bool IsFalse(JsonElement element) =>
        element.ValueKind == JsonValueKind.False ||
        (element.ValueKind == JsonValueKind.String &&
            bool.TryParse(element.GetString(), out var value) && !value);

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
        logger.LogStructuredOutput(json);
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

    private sealed record PluginEntry(
        int Index,
        JsonElement Element,
        string? Name,
        bool Enabled,
        string? PluginPath,
        string? ConfigSection,
        string? Error);
}
