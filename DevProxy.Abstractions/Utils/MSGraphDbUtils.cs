// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.OpenApi.Models;
using Microsoft.OpenApi.Readers;

namespace DevProxy.Abstractions.Utils;

public static class MSGraphDbUtils
{
    private static readonly Dictionary<string, OpenApiDocument> _openApiDocuments = [];
    private static readonly string[] graphVersions = ["v1.0", "beta"];

    private static SqliteConnection? _msGraphDbConnection;

    // v1 refers to v1 of the db schema, not the graph version
    public static string MSGraphDbFilePath => Path.Combine(ProxyUtils.AppFolder!, "msgraph-openapi-v1.db");

    public static SqliteConnection MSGraphDbConnection
    {
        get
        {
            if (_msGraphDbConnection is null)
            {
                _msGraphDbConnection = new($"Data Source={MSGraphDbFilePath}");
                _msGraphDbConnection.Open();
            }

            return _msGraphDbConnection;
        }
    }

    public static async Task<int> GenerateMSGraphDbAsync(ILogger logger, bool skipIfUpdatedToday, CancellationToken cancellationToken)
    {
        var appFolder = ProxyUtils.AppFolder;
        if (string.IsNullOrEmpty(appFolder))
        {
            logger.LogError("App folder {AppFolder} not found", appFolder);
            return 1;
        }

        try
        {
            var dbFileInfo = new FileInfo(MSGraphDbFilePath);
            var modifiedToday = dbFileInfo.Exists && dbFileInfo.LastWriteTime.Date == DateTime.Now.Date;
            if (modifiedToday && skipIfUpdatedToday)
            {
                logger.LogInformation("Microsoft Graph database already updated today");
                return 1;
            }

            await UpdateOpenAPIGraphFilesIfNecessaryAsync(appFolder, logger, cancellationToken);
            await LoadOpenAPIFilesAsync(appFolder, logger, cancellationToken);
            if (_openApiDocuments.Count < 1)
            {
                logger.LogDebug("No OpenAPI files found or couldn't load them");
                return 1;
            }

            var dbConnection = MSGraphDbConnection;
            await CreateDbAsync(dbConnection, logger, cancellationToken);
            await FillDataAsync(dbConnection, logger, cancellationToken);

            logger.LogInformation("Microsoft Graph database successfully updated");

            return 0;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error generating Microsoft Graph database");
            return 1;
        }

    }

    private static string GetGraphOpenApiYamlFileName(string version) => $"graph-{version.Replace(".", "_", StringComparison.OrdinalIgnoreCase)}-openapi.yaml";

    private static async Task CreateDbAsync(SqliteConnection dbConnection, ILogger logger, CancellationToken cancellationToken)
    {
        logger.LogInformation("Creating database...");

        logger.LogDebug("Dropping endpoints table...");
        var dropTable = dbConnection.CreateCommand();
        dropTable.CommandText = "DROP TABLE IF EXISTS endpoints";
        _ = await dropTable.ExecuteNonQueryAsync(cancellationToken);

        logger.LogDebug("Creating endpoints table...");
        var createTable = dbConnection.CreateCommand();
        // when you change the schema, increase the db version number in ProxyUtils
        createTable.CommandText = "CREATE TABLE IF NOT EXISTS endpoints (path TEXT, graphVersion TEXT, hasSelect BOOLEAN)";
        _ = await createTable.ExecuteNonQueryAsync(cancellationToken);

        logger.LogDebug("Creating index on endpoints and version...");
        // Add an index on the path and graphVersion columns
        var createIndex = dbConnection.CreateCommand();
        createIndex.CommandText = "CREATE INDEX IF NOT EXISTS idx_endpoints_path_version ON endpoints (path, graphVersion)";
        _ = await createIndex.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task FillDataAsync(SqliteConnection dbConnection, ILogger logger, CancellationToken cancellationToken)
    {
        logger.LogInformation("Filling database...");

        var i = 0;

        foreach (var openApiDocument in _openApiDocuments)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var graphVersion = openApiDocument.Key;
            var document = openApiDocument.Value;

            logger.LogDebug("Filling database for {GraphVersion}...", graphVersion);

            var insertEndpoint = dbConnection.CreateCommand();
            insertEndpoint.CommandText = "INSERT INTO endpoints (path, graphVersion, hasSelect) VALUES (@path, @graphVersion, @hasSelect)";
            _ = insertEndpoint.Parameters.Add(new("@path", null));
            _ = insertEndpoint.Parameters.Add(new("@graphVersion", null));
            _ = insertEndpoint.Parameters.Add(new("@hasSelect", null));

            foreach (var path in document.Paths)
            {
                cancellationToken.ThrowIfCancellationRequested();

                logger.LogTrace("Endpoint {GraphVersion}{Key}...", graphVersion, path.Key);

                // Get the GET operation for this path
                var getOperation = path.Value.Operations.FirstOrDefault(o => o.Key == OperationType.Get).Value;
                if (getOperation == null)
                {
                    logger.LogTrace("No GET operation found for {GraphVersion}{Key}", graphVersion, path.Key);
                    continue;
                }

                // Check if the GET operation has a $select parameter
                var hasSelect = getOperation.Parameters.Any(p => p.Name == "$select");

                logger.LogTrace("Inserting endpoint {GraphVersion}{Key} with hasSelect={HasSelect}...", graphVersion, path.Key, hasSelect);
                insertEndpoint.Parameters["@path"].Value = path.Key;
                insertEndpoint.Parameters["@graphVersion"].Value = graphVersion;
                insertEndpoint.Parameters["@hasSelect"].Value = hasSelect;
                _ = await insertEndpoint.ExecuteNonQueryAsync(cancellationToken);
                i++;
            }
        }

        logger.LogInformation("Inserted {EndpointCount} endpoints in the database", i);
    }

    private static async Task UpdateOpenAPIGraphFilesIfNecessaryAsync(string folder, ILogger logger, CancellationToken cancellationToken)
    {
        logger.LogInformation("Checking for updated OpenAPI files...");

        foreach (var version in graphVersions)
        {
            try
            {
                var file = new FileInfo(Path.Combine(folder, GetGraphOpenApiYamlFileName(version)));
                logger.LogDebug("Checking for updated OpenAPI file {File}...", file);
                if (file.Exists && file.LastWriteTime.Date == DateTime.Now.Date)
                {
                    logger.LogInformation("File {File} already updated today", file);
                    continue;
                }

                var url = $"https://raw.githubusercontent.com/microsoftgraph/msgraph-metadata/master/openapi/{version}/openapi.yaml";
                logger.LogInformation("Downloading OpenAPI file from {Url}...", url);

                using var client = new HttpClient();
                var response = await client.GetStringAsync(url, cancellationToken);
                await File.WriteAllTextAsync(file.FullName, response, cancellationToken);

                logger.LogDebug("Downloaded OpenAPI file from {Url} to {File}", url, file);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error updating OpenAPI files");
            }
        }
    }

    private static async Task LoadOpenAPIFilesAsync(string folder, ILogger logger, CancellationToken cancellationToken)
    {
        logger.LogInformation("Loading OpenAPI files...");

        foreach (var version in graphVersions)
        {
            var filePath = Path.Combine(folder, GetGraphOpenApiYamlFileName(version));
            var file = new FileInfo(filePath);
            logger.LogDebug("Loading OpenAPI file for {FilePath}...", filePath);

            if (!file.Exists)
            {
                logger.LogDebug("File {FilePath} does not exist", filePath);
                continue;
            }

            try
            {
                var openApiDocument = await new OpenApiStreamReader().ReadAsync(file.OpenRead(), cancellationToken);
                _openApiDocuments[version] = openApiDocument.OpenApiDocument;

                logger.LogDebug("Added OpenAPI file {FilePath} for {Version}", filePath, version);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error loading OpenAPI file {FilePath}", filePath);
            }
        }
    }
}