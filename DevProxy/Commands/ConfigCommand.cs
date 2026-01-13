// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Abstractions.Proxy;
using DevProxy.Abstractions.Utils;
using System.CommandLine;
using System.CommandLine.Parsing;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;

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
            if (configId != null)
            {
                await DownloadConfigAsync(configId);
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
            await CreateConfigFileAsync(name);
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

        this.AddCommands(new List<Command>
        {
            configGetCommand,
            configNewCommand,
            configOpenCommand
        }.OrderByName());
    }

    private async Task DownloadConfigAsync(string configId)
    {
        try
        {
            var appFolder = ProxyUtils.AppFolder;
            if (string.IsNullOrEmpty(appFolder) || !Directory.Exists(appFolder))
            {
                _logger.LogError("App folder {AppFolder} not found", appFolder);
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

            _logger.LogInformation("Downloading config {ConfigId}...", configId);

            var sampleFiles = await GetFilesToDownloadAsync(configId);
            if (sampleFiles.Length == 0)
            {
                _logger.LogError("Config {ConfigId} not found in the samples repo", configId);
                return;
            }
            foreach (var sampleFile in sampleFiles)
            {
                await DownloadFileAsync(sampleFile, targetFolderPath, configId);
            }

            _logger.LogInformation("Config saved in {TargetFolderPath}\r\n", targetFolderPath);
            var configInfo = GetConfigInfo(targetFolderPath);
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
            _logger.LogError(ex, "Error downloading config");
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

    private async Task CreateConfigFileAsync(string name)
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
                _logger.LogError("Snippet {SnippetName} not found", configFileSnippetName);
                return;
            }

            if (snippet.Body is null || snippet.Body.Length == 0)
            {
                _logger.LogError("Snippet {SnippetName} is empty", configFileSnippetName);
                return;
            }

            var snippetBody = GetSnippetBody(snippet.Body);
            var targetFileName = GetTargetFileName(name);
            await File.WriteAllTextAsync(targetFileName, snippetBody);
            _logger.LogInformation("Config file created at {TargetFileName}", targetFileName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error downloading config");
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
}