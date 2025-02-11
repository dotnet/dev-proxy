// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Microsoft.DevProxy.Abstractions;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Microsoft.DevProxy.CommandHandlers;

class ProxyPresetInfo
{
    public IList<string> ConfigFiles { get; set; } = [];
    public IList<string> MockFiles { get; set; } = [];
}

class GitHubTreeResponse
{
    public GitHubTreeItem[] Tree { get; set; } = [];
    public bool Truncated { get; set; }
}

class GitHubTreeItem
{
    public string Path { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
}

public static class PresetGetCommandHandler
{
    public static async Task DownloadPresetAsync(string presetId, ILogger logger)
    {
        try
        {
            var appFolder = ProxyUtils.AppFolder;
            if (string.IsNullOrEmpty(appFolder) || !Directory.Exists(appFolder))
            {
                logger.LogError("App folder {appFolder} not found", appFolder);
                return;
            }

            var presetsFolderPath = Path.Combine(appFolder, "presets");
            logger.LogDebug("Checking if presets folder {presetsFolderPath} exists...", presetsFolderPath);
            if (!Directory.Exists(presetsFolderPath))
            {
                logger.LogDebug("Presets folder not found, creating it...");
                Directory.CreateDirectory(presetsFolderPath);
                logger.LogDebug("Presets folder created");
            }

            logger.LogDebug("Getting target folder path for preset {presetId}...", presetId);
            var targetFolderPath = GetTargetFolderPath(appFolder, presetId);
            logger.LogDebug("Creating target folder {targetFolderPath}...", targetFolderPath);
            Directory.CreateDirectory(targetFolderPath);

            logger.LogInformation("Downloading preset {presetId}...", presetId);

            var sampleFiles = await GetFilesToDownloadAsync(presetId, logger);
            foreach (var sampleFile in sampleFiles)
            {
                await DownloadFileAsync(sampleFile, targetFolderPath, presetId, logger);
            }

            logger.LogInformation("Preset saved in {targetFolderPath}\r\n", targetFolderPath);
            var presetInfo = GetPresetInfo(targetFolderPath, logger);
            if (!presetInfo.ConfigFiles.Any() && !presetInfo.MockFiles.Any())
            {
                return;
            }

            if (presetInfo.ConfigFiles.Any())
            {
                logger.LogInformation("To start Dev Proxy with the preset, run:");
                foreach (var configFile in presetInfo.ConfigFiles)
                {
                    logger.LogInformation("  devproxy --config-file \"{configFile}\"", configFile.Replace(appFolder, "~appFolder"));
                }
            }
            else
            {
                logger.LogInformation("To start Dev Proxy with the mock file, enable the MockResponsePlugin or GraphMockResponsePlugin and run:");
                foreach (var mockFile in presetInfo.MockFiles)
                {
                    logger.LogInformation("  devproxy --mock-file \"{mockFile}\"", mockFile.Replace(appFolder, "~appFolder"));
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error downloading presets");
        }
    }

    /// <summary>
    /// Returns the list of files that can be used as entry points for the preset
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
    /// <param name="presetFolder">Full path to the folder with preset files</param>
    /// <returns>Array of files that can be used to start proxy with</returns>
    private static ProxyPresetInfo GetPresetInfo(string presetFolder, ILogger logger)
    {
        var presetInfo = new ProxyPresetInfo();

        logger.LogDebug("Getting list of JSON files in {presetFolder}...", presetFolder);
        var jsonFiles = Directory.GetFiles(presetFolder, "*.json");
        if (!jsonFiles.Any())
        {
            logger.LogDebug("No JSON files found");
            return presetInfo;
        }

        foreach (var jsonFile in jsonFiles)
        {
            logger.LogDebug("Reading file {jsonFile}...", jsonFile);

            var fileContents = File.ReadAllText(jsonFile);
            if (fileContents.Contains("\"plugins\":"))
            {
                logger.LogDebug("File {jsonFile} contains proxy config", jsonFile);
                presetInfo.ConfigFiles.Add(jsonFile);
                continue;
            }

            if (fileContents.Contains("\"responses\":"))
            {
                logger.LogDebug("File {jsonFile} contains mock data", jsonFile);
                presetInfo.MockFiles.Add(jsonFile);
                continue;
            }

            logger.LogDebug("File {jsonFile} is not a proxy config or mock data", jsonFile);
        }

        if (presetInfo.ConfigFiles.Any())
        {
            logger.LogDebug("Found {configFilesCount} proxy config files. Clearing mocks...", presetInfo.ConfigFiles.Count);
            presetInfo.MockFiles.Clear();
        }

        return presetInfo;
    }

    private static string GetTargetFolderPath(string appFolder, string presetId)
    {
        var baseFolder = Path.Combine(appFolder, "presets", presetId);
        var newFolder = baseFolder;
        var i = 1;
        while (Directory.Exists(newFolder))
        {
            newFolder = baseFolder + i++;
        }

        return newFolder;
    }

    private static async Task<string[]> GetFilesToDownloadAsync(string sampleFolderName, ILogger logger)
    {
        logger.LogDebug("Getting list of files in Dev Proxy samples repo...");
        var url = $"https://api.github.com/repos/pnp/proxy-samples/git/trees/main?recursive=1";
        using var client = new HttpClient();
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("dev-proxy", ProxyUtils.ProductVersion));
        var response = await client.GetAsync(url);

        if (response.IsSuccessStatusCode)
        {
            var content = await response.Content.ReadAsStringAsync();
            var tree = JsonSerializer.Deserialize<GitHubTreeResponse>(content, ProxyUtils.JsonSerializerOptions);
            if (tree is null)
            {
                throw new Exception("Failed to get list of files from GitHub");
            }

            var samplePath = $"samples/{sampleFolderName}";

            var filesToDownload = tree.Tree
                .Where(f => f.Path.StartsWith(samplePath, StringComparison.OrdinalIgnoreCase) && f.Type == "blob")
                .Select(f => f.Path)
                .ToArray();

            foreach (var file in filesToDownload)
            {
                logger.LogDebug("Found file {file}", file);
            }

            return filesToDownload;
        }
        else
        {
            throw new Exception($"Failed to get list of files from GitHub. Status code: {response.StatusCode}");
        }
    }

    private static async Task DownloadFileAsync(string filePath, string targetFolderPath, string presetId, ILogger logger)
    {
        var url = $"https://raw.githubusercontent.com/pnp/proxy-samples/main/{filePath.Replace("#", "%23")}";
        logger.LogDebug("Downloading file {filePath}...", filePath);

        using var client = new HttpClient();
        var response = await client.GetAsync(url);

        if (response.IsSuccessStatusCode)
        {
            var contentStream = await response.Content.ReadAsStreamAsync();
            var filePathInsideSample = Path.GetRelativePath($"samples/{presetId}", filePath);
            var directoryNameInsideSample = Path.GetDirectoryName(filePathInsideSample);
            if (directoryNameInsideSample is not null)
            {
                Directory.CreateDirectory(Path.Combine(targetFolderPath, directoryNameInsideSample));
            }
            var localFilePath = Path.Combine(targetFolderPath, filePathInsideSample);

            using var fileStream = new FileStream(localFilePath, FileMode.Create, FileAccess.Write, FileShare.None);
            await contentStream.CopyToAsync(fileStream);

            logger.LogDebug("File downloaded successfully to {localFilePath}", localFilePath);
        }
        else
        {
            throw new Exception($"Failed to download file {url}. Status code: {response.StatusCode}");
        }
    }
}
