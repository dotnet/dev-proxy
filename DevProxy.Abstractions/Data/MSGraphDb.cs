// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Abstractions.Utils;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.OpenApi.Models;
using Microsoft.OpenApi.Readers;

namespace DevProxy.Abstractions.Data;

public sealed class MSGraphDb(HttpClient httpClient, ILogger<MSGraphDb> logger) : IDisposable
{
    private static readonly string[] graphVersions = ["v1.0", "beta"];
    private readonly Dictionary<string, OpenApiDocument> _openApiDocuments = [];
#pragma warning disable CA2213 // Disposable fields should be disposed
    private readonly HttpClient _httpClient = httpClient;
#pragma warning restore CA2213 // Disposable fields should be disposed
    private readonly ILogger<MSGraphDb> _logger = logger;
    private SqliteConnection? _connection;

    // v1 refers to v1 of the db schema, not the graph version
    public static string MSGraphDbFilePath => Path.Combine(ProxyUtils.AppFolder!, "msgraph-openapi-v1.db");

    public SqliteConnection Connection
    {
        get
        {
            if (_connection is null)
            {
                _connection = new($"Data Source={MSGraphDbFilePath}");
                _connection.Open();
            }

            return _connection;
        }
    }

    public async Task<int> GenerateDbAsync(bool skipIfUpdatedToday, CancellationToken cancellationToken)
    {
        var appFolder = ProxyUtils.AppFolder;
        if (string.IsNullOrEmpty(appFolder))
        {
            _logger.LogError("App folder {AppFolder} not found", appFolder);
            return 1;
        }

        try
        {
            var dbFileInfo = new FileInfo(MSGraphDbFilePath);
            var modifiedToday = dbFileInfo.Exists && dbFileInfo.LastWriteTime.Date == DateTime.Now.Date;
            if (modifiedToday && skipIfUpdatedToday)
            {
                _logger.LogInformation("Microsoft Graph database already updated today");
                return 1;
            }

            await UpdateOpenAPIGraphFilesIfNecessaryAsync(appFolder, cancellationToken);
            await LoadOpenAPIFilesAsync(appFolder, cancellationToken);
            if (_openApiDocuments.Count < 1)
            {
                _logger.LogDebug("No OpenAPI files found or couldn't load them");
                return 1;
            }

            await CreateDbAsync(cancellationToken);

            SetDbJournaling(false);
            await FillDataAsync(cancellationToken);
            SetDbJournaling(true);


            _logger.LogInformation("Microsoft Graph database successfully updated");

            return 0;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error generating Microsoft Graph database");
            return 1;
        }

    }

    private static string GetGraphOpenApiYamlFileName(string version) => $"graph-{version.Replace(".", "_", StringComparison.OrdinalIgnoreCase)}-openapi.yaml";

    private async Task CreateDbAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Creating database...");

        _logger.LogDebug("Dropping endpoints table...");
        var dropTable = Connection.CreateCommand();
        dropTable.CommandText = "DROP TABLE IF EXISTS endpoints";
        _ = await dropTable.ExecuteNonQueryAsync(cancellationToken);

        _logger.LogDebug("Creating endpoints table...");
        var createTable = Connection.CreateCommand();
        // when you change the schema, increase the db version number in ProxyUtils
        createTable.CommandText = "CREATE TABLE IF NOT EXISTS endpoints (path TEXT, graphVersion TEXT, hasSelect BOOLEAN)";
        _ = await createTable.ExecuteNonQueryAsync(cancellationToken);

        _logger.LogDebug("Creating index on endpoints and version...");
        // Add an index on the path and graphVersion columns
        var createIndex = Connection.CreateCommand();
        createIndex.CommandText = "CREATE INDEX IF NOT EXISTS idx_endpoints_path_version ON endpoints (path, graphVersion)";
        _ = await createIndex.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task FillDataAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Filling database...");
        
        await using var transaction = await Connection.BeginTransactionAsync(cancellationToken);

        var i = 0;

        foreach (var openApiDocument in _openApiDocuments)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var graphVersion = openApiDocument.Key;
            var document = openApiDocument.Value;

            _logger.LogDebug("Filling database for {GraphVersion}...", graphVersion);

            var insertEndpoint = Connection.CreateCommand();
            insertEndpoint.CommandText = "INSERT INTO endpoints (path, graphVersion, hasSelect) VALUES (@path, @graphVersion, @hasSelect)";
            _ = insertEndpoint.Parameters.Add(new("@path", null));
            _ = insertEndpoint.Parameters.Add(new("@graphVersion", null));
            _ = insertEndpoint.Parameters.Add(new("@hasSelect", null));

            foreach (var path in document.Paths)
            {
                cancellationToken.ThrowIfCancellationRequested();

                _logger.LogTrace("Endpoint {GraphVersion}{Key}...", graphVersion, path.Key);

                // Get the GET operation for this path
                var getOperation = path.Value.Operations.FirstOrDefault(o => o.Key == OperationType.Get).Value;
                if (getOperation == null)
                {
                    _logger.LogTrace("No GET operation found for {GraphVersion}{Key}", graphVersion, path.Key);
                    continue;
                }

                // Check if the GET operation has a $select parameter
                var hasSelect = getOperation.Parameters.Any(p => p.Name == "$select");

                _logger.LogTrace("Inserting endpoint {GraphVersion}{Key} with hasSelect={HasSelect}...", graphVersion, path.Key, hasSelect);
                insertEndpoint.Parameters["@path"].Value = path.Key;
                insertEndpoint.Parameters["@graphVersion"].Value = graphVersion;
                insertEndpoint.Parameters["@hasSelect"].Value = hasSelect;
                _ = await insertEndpoint.ExecuteNonQueryAsync(cancellationToken);
                i++;
            }
        }

        await transaction.CommitAsync(cancellationToken);

        _logger.LogInformation("Inserted {EndpointCount} endpoints in the database", i);
    }

    private async Task UpdateOpenAPIGraphFilesIfNecessaryAsync(string folder, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Checking for updated OpenAPI files...");

        foreach (var version in graphVersions)
        {
            try
            {
                var file = new FileInfo(Path.Combine(folder, GetGraphOpenApiYamlFileName(version)));
                _logger.LogDebug("Checking for updated OpenAPI file {File}...", file);
                if (file.Exists && file.LastWriteTime.Date == DateTime.Now.Date)
                {
                    _logger.LogInformation("File {File} already updated today", file);
                    continue;
                }

                var url = $"https://raw.githubusercontent.com/microsoftgraph/msgraph-metadata/master/openapi/{version}/openapi.yaml";
                _logger.LogInformation("Downloading OpenAPI file from {Url}...", url);

                var response = await _httpClient.GetStringAsync(url, cancellationToken);
                await File.WriteAllTextAsync(file.FullName, response, cancellationToken);

                _logger.LogDebug("Downloaded OpenAPI file from {Url} to {File}", url, file);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error updating OpenAPI files");
            }
        }
    }

    private async Task LoadOpenAPIFilesAsync(string folder, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Loading OpenAPI files...");

        foreach (var version in graphVersions)
        {
            var filePath = Path.Combine(folder, GetGraphOpenApiYamlFileName(version));
            var file = new FileInfo(filePath);
            _logger.LogDebug("Loading OpenAPI file for {FilePath}...", filePath);

            if (!file.Exists)
            {
                _logger.LogDebug("File {FilePath} does not exist", filePath);
                continue;
            }

            try
            {
                var openApiDocument = await new OpenApiStreamReader().ReadAsync(file.OpenRead(), cancellationToken);
                _openApiDocuments[version] = openApiDocument.OpenApiDocument;

                _logger.LogDebug("Added OpenAPI file {FilePath} for {Version}", filePath, version);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error loading OpenAPI file {FilePath}", filePath);
            }
        }
    }

    private void SetDbJournaling(bool enabled)
    {
        using var command = Connection.CreateCommand();
        if (enabled)
        {
            command.CommandText = "PRAGMA journal_mode = DELETE;";
            _ = command.ExecuteNonQuery();
        }
        else
        {
            command.CommandText = "PRAGMA journal_mode = OFF;";
            _ = command.ExecuteNonQuery();
        }
    }

    public void Dispose()
    {
        _connection?.Dispose();
    }
}