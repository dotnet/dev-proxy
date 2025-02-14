// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Abstractions;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DevProxy.CommandHandlers;

public class VisualStudioCodeSnippet
{
    public string? Prefix { get; set; }
    public string[]? Body { get; set; }
    public string? Description { get; set; }
}

public static class ConfigNewCommandHandler
{
    private static readonly string snippetsFileUrl = $"https://aka.ms/devproxy/snippets/v{ProxyUtils.ProductVersion}";
    private static readonly string configFileSnippetName = "ConfigFile";

    public static async Task CreateConfigFileAsync(string name, ILogger logger)
    {
        try
        {
            var snippets = await DownloadSnippetsAsync(logger);
            if (snippets is null)
            {
                return;
            }

            if (!snippets.TryGetValue(configFileSnippetName, out var snippet))
            {
                logger.LogError("Snippet {snippetName} not found", configFileSnippetName);
                return;
            }

            if (snippet.Body is null || snippet.Body.Length == 0)
            {
                logger.LogError("Snippet {snippetName} is empty", configFileSnippetName);
                return;
            }

            var snippetBody = GetSnippetBody(snippet.Body);
            var targetFileName = GetTargetFileName(name, logger);
            File.WriteAllText(targetFileName, snippetBody);
            logger.LogInformation("Config file created at {targetFileName}", targetFileName);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error downloading config");
        }
    }

    private static string? GetSnippetBody(string[] bodyLines)
    {
        var body = string.Join("\n", bodyLines);
        // unescape $
        body = body.Replace("\\$", "$");
        // remove snippet $n markers
        body = Regex.Replace(body, @"\$[0-9]+", "");
        return body;
    }

    private static async Task<Dictionary<string, VisualStudioCodeSnippet>?> DownloadSnippetsAsync(ILogger logger)
    {
        logger.LogDebug("Downloading snippets from {snippetsFileUrl}...", snippetsFileUrl);
        using var client = new HttpClient();
        var response = await client.GetAsync(snippetsFileUrl);
        if (response.IsSuccessStatusCode)
        {
            var content = await response.Content.ReadAsStringAsync();
            try
            {
                return JsonSerializer.Deserialize<Dictionary<string, VisualStudioCodeSnippet>>(content, ProxyUtils.JsonSerializerOptions);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to parse snippets from {Url}", snippetsFileUrl);
                return null;
            }
        }
        else
        {
            logger.LogError("Failed to download snippets. Status code: {statusCode}", response.StatusCode);
            return null;
        }
    }

    private static string GetTargetFileName(string name, ILogger logger)
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
            logger.LogDebug("File {name} already exists. Trying {newName}...", name, newName);
            name = newName;
        }
    }
}
