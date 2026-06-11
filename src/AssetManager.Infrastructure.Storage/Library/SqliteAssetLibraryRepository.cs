using System.Globalization;
using System.Text.RegularExpressions;
using AssetManager.Application.Library;
using AssetManager.Domain.Library;
using Microsoft.Data.Sqlite;

namespace AssetManager.Infrastructure.Storage.Library;

public sealed partial class SqliteAssetLibraryRepository : IAssetLibraryRepository
{
    private const int SchemaVersion = 1;
    // SQLite expression tree depth limit is 1000 nodes. Each path in GetByRelativePathsAsync
    // adds 1 OR term; each path in GetByRelativePathPrefixesAsync adds ~2 terms (equality + LIKE).
    // A batch size of 400 keeps both well under the limit (400 for paths, 800 for prefixes).
    private const int QueryBatchSize = 400;

    public async Task InitializeAsync(LibraryLocation location, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(location.ManagementPath);

        await using var connection = await OpenConnectionAsync(location, cancellationToken);
        await ExecuteNonQueryAsync(connection, """
            PRAGMA foreign_keys = ON;

            CREATE TABLE IF NOT EXISTS schema_migrations (
                version INTEGER PRIMARY KEY,
                applied_at TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS library_settings (
                key TEXT PRIMARY KEY,
                value TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS assets (
                id TEXT PRIMARY KEY,
                display_name TEXT NOT NULL,
                library_relative_path TEXT NOT NULL,
                source_path TEXT NULL,
                kind TEXT NOT NULL,
                extension TEXT NOT NULL,
                size_bytes INTEGER NOT NULL,
                created_at TEXT NOT NULL,
                modified_at TEXT NOT NULL,
                imported_at TEXT NOT NULL,
                content_hash TEXT NOT NULL,
                notes TEXT NOT NULL DEFAULT '',
                status TEXT NOT NULL
            );

            CREATE UNIQUE INDEX IF NOT EXISTS ux_assets_available_path
                ON assets(library_relative_path)
                WHERE status <> 'Missing';

            CREATE TABLE IF NOT EXISTS tags (
                id TEXT PRIMARY KEY,
                name TEXT NOT NULL,
                normalized_name TEXT NOT NULL UNIQUE
            );

            CREATE TABLE IF NOT EXISTS asset_tags (
                asset_id TEXT NOT NULL REFERENCES assets(id) ON DELETE CASCADE,
                tag_id TEXT NOT NULL REFERENCES tags(id) ON DELETE CASCADE,
                PRIMARY KEY(asset_id, tag_id)
            );

            CREATE VIRTUAL TABLE IF NOT EXISTS asset_search USING fts5(
                asset_id UNINDEXED,
                display_name,
                library_relative_path,
                extension,
                kind,
                tags,
                notes
            );
            """, cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT OR IGNORE INTO schema_migrations(version, applied_at)
            VALUES ($version, $appliedAt);

            INSERT OR IGNORE INTO library_settings(key, value)
            VALUES ('schema_version', $versionText);
            """;
        command.Parameters.AddWithValue("$version", SchemaVersion);
        command.Parameters.AddWithValue("$appliedAt", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$versionText", SchemaVersion.ToString(CultureInfo.InvariantCulture));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<AssetRecord>> AddAssetsAsync(
        LibraryLocation location,
        IEnumerable<PreparedAssetFile> assets,
        CancellationToken cancellationToken = default)
    {
        var preparedAssets = assets.ToArray();
        if (preparedAssets.Length == 0)
        {
            return [];
        }

        await using var connection = await OpenConnectionAsync(location, cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);

        var records = new List<AssetRecord>();
        foreach (var asset in preparedAssets)
        {
            var record = new AssetRecord(
                Guid.NewGuid(),
                asset.DisplayName,
                asset.LibraryRelativePath,
                asset.SourcePath,
                asset.TypeId,
                asset.Extension,
                asset.SizeBytes,
                asset.CreatedAt,
                asset.ModifiedAt,
                asset.ImportedAt,
                asset.ContentHash,
                string.Empty,
                AssetStatus.Available,
                []);

            await InsertAssetAsync(connection, transaction, record, cancellationToken);
            await RebuildSearchRowAsync(connection, transaction, record.Id, cancellationToken);
            records.Add(record);
        }

        await transaction.CommitAsync(cancellationToken);
        return records;
    }

    public async Task<IReadOnlyList<AssetRecord>> SearchAsync(
        LibraryLocation location,
        AssetSearchRequest request,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(location, cancellationToken);
        await using var command = connection.CreateCommand();

        var whereParts = new List<string> { "1 = 1" };
        if (!request.CurrentFolder.IsRoot)
        {
            whereParts.Add("a.library_relative_path LIKE $folderPrefix ESCAPE '\\'");
            command.Parameters.AddWithValue("$folderPrefix", EscapeLike(request.CurrentFolder.Value + "/") + "%");
        }

        var likeQuery = $"%{EscapeLike(request.Query)}%";
        var ftsQuery = BuildFtsQuery(request.Query);
        if (!string.IsNullOrWhiteSpace(request.Query))
        {
            if (string.IsNullOrWhiteSpace(ftsQuery))
            {
                whereParts.Add(BuildLikeSearchClause());
            }
            else
            {
                whereParts.Add($"""
                    (
                        a.id IN (SELECT asset_id FROM asset_search WHERE asset_search MATCH $ftsQuery)
                        OR {BuildLikeSearchClause()}
                    )
                    """);
                command.Parameters.AddWithValue("$ftsQuery", ftsQuery);
            }

            command.Parameters.AddWithValue("$likeQuery", likeQuery);
        }

        for (var index = 0; index < request.RequiredTags.Count; index++)
        {
            var parameterName = "$requiredTag" + index.ToString(CultureInfo.InvariantCulture);
            whereParts.Add($"""
                EXISTS (
                    SELECT 1
                    FROM asset_tags required_at
                    INNER JOIN tags required_t ON required_t.id = required_at.tag_id
                    WHERE required_at.asset_id = a.id
                    AND required_t.normalized_name = {parameterName}
                )
                """);
            command.Parameters.AddWithValue(parameterName, NormalizeTag(request.RequiredTags[index]));
        }

        command.CommandText = $"""
            SELECT
                a.id,
                a.display_name,
                a.library_relative_path,
                a.source_path,
                a.kind,
                a.extension,
                a.size_bytes,
                a.created_at,
                a.modified_at,
                a.imported_at,
                a.content_hash,
                a.notes,
                a.status,
                COALESCE((
                    SELECT group_concat(ordered_tags.name, ', ')
                    FROM (
                        SELECT t.name
                        FROM asset_tags at
                        INNER JOIN tags t ON t.id = at.tag_id
                        WHERE at.asset_id = a.id
                        ORDER BY t.name COLLATE NOCASE
                    ) ordered_tags
                ), '') AS tags
            FROM assets a
            WHERE {string.Join(" AND ", whereParts)}
            ORDER BY a.imported_at DESC, a.display_name COLLATE NOCASE;
            """;

        return await ReadAssetsAsync(command, cancellationToken);
    }

    public async Task<IReadOnlyList<AssetRecord>> GetAllAsync(
        LibraryLocation location,
        CancellationToken cancellationToken = default)
    {
        return await SearchAsync(
            location,
            new AssetSearchRequest(LibraryRelativePath.Root, string.Empty, []),
            cancellationToken);
    }

    public async Task<AssetRecord?> GetByIdAsync(
        LibraryLocation location,
        Guid assetId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(location, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                a.id,
                a.display_name,
                a.library_relative_path,
                a.source_path,
                a.kind,
                a.extension,
                a.size_bytes,
                a.created_at,
                a.modified_at,
                a.imported_at,
                a.content_hash,
                a.notes,
                a.status,
                COALESCE((
                    SELECT group_concat(ordered_tags.name, ', ')
                    FROM (
                        SELECT t.name
                        FROM asset_tags at
                        INNER JOIN tags t ON t.id = at.tag_id
                        WHERE at.asset_id = a.id
                        ORDER BY t.name COLLATE NOCASE
                    ) ordered_tags
                ), '') AS tags
            FROM assets a
            WHERE a.id = $id;
            """;
        command.Parameters.AddWithValue("$id", assetId.ToString("D"));

        var assets = await ReadAssetsAsync(command, cancellationToken);
        return assets.SingleOrDefault();
    }

    public async Task<IReadOnlyList<AssetRecord>> GetByRelativePathsAsync(
        LibraryLocation location,
        IEnumerable<LibraryRelativePath> paths,
        CancellationToken cancellationToken = default)
    {
        var normalizedPaths = paths
            .Select(path => path.Value)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (normalizedPaths.Length == 0)
        {
            return [];
        }

        var allAssets = new List<AssetRecord>();
        await using var connection = await OpenConnectionAsync(location, cancellationToken);

        foreach (var batch in normalizedPaths.Chunk(QueryBatchSize))
        {
            await using var command = connection.CreateCommand();
            var clauses = new List<string>();
            for (var index = 0; index < batch.Length; index++)
            {
                var parameterName = "$path" + index.ToString(CultureInfo.InvariantCulture);
                clauses.Add("a.library_relative_path = " + parameterName);
                command.Parameters.AddWithValue(parameterName, batch[index]);
            }

            command.CommandText = BuildAssetSelectSql(string.Join(" OR ", clauses));
            allAssets.AddRange(await ReadAssetsAsync(command, cancellationToken));
        }

        return allAssets;
    }

    public async Task<IReadOnlyList<AssetRecord>> GetByRelativePathPrefixesAsync(
        LibraryLocation location,
        IEnumerable<LibraryRelativePath> paths,
        CancellationToken cancellationToken = default)
    {
        var normalizedPaths = paths
            .DistinctBy(path => path.Value, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (normalizedPaths.Length == 0)
        {
            return [];
        }

        var allAssets = new List<AssetRecord>();
        await using var connection = await OpenConnectionAsync(location, cancellationToken);

        foreach (var batch in normalizedPaths.Chunk(QueryBatchSize))
        {
            await using var command = connection.CreateCommand();
            var clauses = new List<string>();
            for (var index = 0; index < batch.Length; index++)
            {
                if (batch[index].IsRoot)
                {
                    clauses.Add("1 = 1");
                    continue;
                }

                var pathParameterName = "$path" + index.ToString(CultureInfo.InvariantCulture);
                var prefixParameterName = "$prefix" + index.ToString(CultureInfo.InvariantCulture);
                clauses.Add($"(a.library_relative_path = {pathParameterName} OR a.library_relative_path LIKE {prefixParameterName} ESCAPE '\\')");
                command.Parameters.AddWithValue(pathParameterName, batch[index].Value);
                command.Parameters.AddWithValue(prefixParameterName, EscapeLike(batch[index].Value + "/") + "%");
            }

            command.CommandText = BuildAssetSelectSql(string.Join(" OR ", clauses));
            allAssets.AddRange(await ReadAssetsAsync(command, cancellationToken));
        }

        return allAssets;
    }

    public async Task UpdateMetadataAsync(
        LibraryLocation location,
        Guid assetId,
        string notes,
        IReadOnlyList<string> tags,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(location, cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);

        await using (var updateCommand = connection.CreateCommand())
        {
            updateCommand.Transaction = transaction;
            updateCommand.CommandText = """
                UPDATE assets
                SET notes = $notes
                WHERE id = $id;
                """;
            updateCommand.Parameters.AddWithValue("$notes", notes);
            updateCommand.Parameters.AddWithValue("$id", assetId.ToString("D"));
            await updateCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var deleteCommand = connection.CreateCommand())
        {
            deleteCommand.Transaction = transaction;
            deleteCommand.CommandText = "DELETE FROM asset_tags WHERE asset_id = $assetId;";
            deleteCommand.Parameters.AddWithValue("$assetId", assetId.ToString("D"));
            await deleteCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var tag in tags)
        {
            var tagId = await GetOrCreateTagAsync(connection, transaction, tag, cancellationToken);
            await using var attachCommand = connection.CreateCommand();
            attachCommand.Transaction = transaction;
            attachCommand.CommandText = """
                INSERT OR IGNORE INTO asset_tags(asset_id, tag_id)
                VALUES ($assetId, $tagId);
                """;
            attachCommand.Parameters.AddWithValue("$assetId", assetId.ToString("D"));
            attachCommand.Parameters.AddWithValue("$tagId", tagId.ToString("D"));
            await attachCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await RebuildSearchRowAsync(connection, transaction, assetId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task UpdateAssetFileStateAsync(
        LibraryLocation location,
        Guid assetId,
        StoredContentFile file,
        AssetStatus status,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(location, cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE assets
            SET display_name = $displayName,
                library_relative_path = $libraryRelativePath,
                kind = $kind,
                extension = $extension,
                size_bytes = $sizeBytes,
                created_at = $createdAt,
                modified_at = $modifiedAt,
                content_hash = $contentHash,
                status = $status
            WHERE id = $id;
            """;
        AddAssetFileStateParameters(command, file, status);
        command.Parameters.AddWithValue("$id", assetId.ToString("D"));
        await command.ExecuteNonQueryAsync(cancellationToken);

        await RebuildSearchRowAsync(connection, transaction, assetId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task MarkAssetStatusAsync(
        LibraryLocation location,
        Guid assetId,
        AssetStatus status,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(location, cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE assets
            SET status = $status
            WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$status", status.ToString());
        command.Parameters.AddWithValue("$id", assetId.ToString("D"));
        await command.ExecuteNonQueryAsync(cancellationToken);

        await RebuildSearchRowAsync(connection, transaction, assetId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task DeleteAssetAsync(
        LibraryLocation location,
        Guid assetId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(location, cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);

        await using (var searchCommand = connection.CreateCommand())
        {
            searchCommand.Transaction = transaction;
            searchCommand.CommandText = "DELETE FROM asset_search WHERE asset_id = $assetId;";
            searchCommand.Parameters.AddWithValue("$assetId", assetId.ToString("D"));
            await searchCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var assetCommand = connection.CreateCommand())
        {
            assetCommand.Transaction = transaction;
            assetCommand.CommandText = "DELETE FROM assets WHERE id = $assetId;";
            assetCommand.Parameters.AddWithValue("$assetId", assetId.ToString("D"));
            await assetCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task InsertAssetAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        AssetRecord asset,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO assets(
                id,
                display_name,
                library_relative_path,
                source_path,
                kind,
                extension,
                size_bytes,
                created_at,
                modified_at,
                imported_at,
                content_hash,
                notes,
                status)
            VALUES(
                $id,
                $displayName,
                $libraryRelativePath,
                $sourcePath,
                $kind,
                $extension,
                $sizeBytes,
                $createdAt,
                $modifiedAt,
                $importedAt,
                $contentHash,
                $notes,
                $status);
            """;
        command.Parameters.AddWithValue("$id", asset.Id.ToString("D"));
        command.Parameters.AddWithValue("$displayName", asset.DisplayName);
        command.Parameters.AddWithValue("$libraryRelativePath", asset.LibraryRelativePath.Value);
        command.Parameters.AddWithValue("$sourcePath", (object?)asset.SourcePath ?? DBNull.Value);
        command.Parameters.AddWithValue("$kind", asset.TypeId.Value);
        command.Parameters.AddWithValue("$extension", asset.Extension);
        command.Parameters.AddWithValue("$sizeBytes", asset.SizeBytes);
        command.Parameters.AddWithValue("$createdAt", FormatDate(asset.CreatedAt));
        command.Parameters.AddWithValue("$modifiedAt", FormatDate(asset.ModifiedAt));
        command.Parameters.AddWithValue("$importedAt", FormatDate(asset.ImportedAt));
        command.Parameters.AddWithValue("$contentHash", asset.ContentHash);
        command.Parameters.AddWithValue("$notes", asset.Notes);
        command.Parameters.AddWithValue("$status", asset.Status.ToString());
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<Guid> GetOrCreateTagAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string tag,
        CancellationToken cancellationToken)
    {
        var normalizedTag = NormalizeTag(tag);

        await using (var insertCommand = connection.CreateCommand())
        {
            insertCommand.Transaction = transaction;
            insertCommand.CommandText = """
                INSERT OR IGNORE INTO tags(id, name, normalized_name)
                VALUES ($id, $name, $normalizedName);
                """;
            insertCommand.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("D"));
            insertCommand.Parameters.AddWithValue("$name", tag);
            insertCommand.Parameters.AddWithValue("$normalizedName", normalizedTag);
            await insertCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var selectCommand = connection.CreateCommand();
        selectCommand.Transaction = transaction;
        selectCommand.CommandText = "SELECT id FROM tags WHERE normalized_name = $normalizedName;";
        selectCommand.Parameters.AddWithValue("$normalizedName", normalizedTag);

        var id = await selectCommand.ExecuteScalarAsync(cancellationToken);
        return Guid.Parse((string)id!);
    }

    private static async Task RebuildSearchRowAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid assetId,
        CancellationToken cancellationToken)
    {
        await using (var deleteCommand = connection.CreateCommand())
        {
            deleteCommand.Transaction = transaction;
            deleteCommand.CommandText = "DELETE FROM asset_search WHERE asset_id = $assetId;";
            deleteCommand.Parameters.AddWithValue("$assetId", assetId.ToString("D"));
            await deleteCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var insertCommand = connection.CreateCommand();
        insertCommand.Transaction = transaction;
        insertCommand.CommandText = """
            INSERT INTO asset_search(asset_id, display_name, library_relative_path, extension, kind, tags, notes)
            SELECT
                a.id,
                a.display_name,
                a.library_relative_path,
                a.extension,
                a.kind,
                COALESCE((
                    SELECT group_concat(ordered_tags.name, ' ')
                    FROM (
                        SELECT t.name
                        FROM asset_tags at
                        INNER JOIN tags t ON t.id = at.tag_id
                        WHERE at.asset_id = a.id
                        ORDER BY t.name COLLATE NOCASE
                    ) ordered_tags
                ), ''),
                a.notes
            FROM assets a
            WHERE a.id = $assetId;
            """;
        insertCommand.Parameters.AddWithValue("$assetId", assetId.ToString("D"));
        await insertCommand.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void AddAssetFileStateParameters(SqliteCommand command, StoredContentFile file, AssetStatus status)
    {
        command.Parameters.AddWithValue("$displayName", file.DisplayName);
        command.Parameters.AddWithValue("$libraryRelativePath", file.LibraryRelativePath.Value);
        command.Parameters.AddWithValue("$kind", file.TypeId.Value);
        command.Parameters.AddWithValue("$extension", file.Extension);
        command.Parameters.AddWithValue("$sizeBytes", file.SizeBytes);
        command.Parameters.AddWithValue("$createdAt", FormatDate(file.CreatedAt));
        command.Parameters.AddWithValue("$modifiedAt", FormatDate(file.ModifiedAt));
        command.Parameters.AddWithValue("$contentHash", file.ContentHash);
        command.Parameters.AddWithValue("$status", status.ToString());
    }

    private static async Task<IReadOnlyList<AssetRecord>> ReadAssetsAsync(
        SqliteCommand command,
        CancellationToken cancellationToken)
    {
        var assets = new List<AssetRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            assets.Add(new AssetRecord(
                Guid.Parse(reader.GetString(0)),
                reader.GetString(1),
                LibraryRelativePath.Create(reader.GetString(2)),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                AssetTypeId.Create(reader.GetString(4)),
                reader.GetString(5),
                reader.GetInt64(6),
                ParseDate(reader.GetString(7)),
                ParseDate(reader.GetString(8)),
                ParseDate(reader.GetString(9)),
                reader.GetString(10),
                reader.GetString(11),
                Enum.Parse<AssetStatus>(reader.GetString(12)),
                SplitTags(reader.GetString(13))));
        }

        return assets;
    }

    private static string BuildAssetSelectSql(string whereClause)
    {
        return $"""
            SELECT
                a.id,
                a.display_name,
                a.library_relative_path,
                a.source_path,
                a.kind,
                a.extension,
                a.size_bytes,
                a.created_at,
                a.modified_at,
                a.imported_at,
                a.content_hash,
                a.notes,
                a.status,
                COALESCE((
                    SELECT group_concat(ordered_tags.name, ', ')
                    FROM (
                        SELECT t.name
                        FROM asset_tags at
                        INNER JOIN tags t ON t.id = at.tag_id
                        WHERE at.asset_id = a.id
                        ORDER BY t.name COLLATE NOCASE
                    ) ordered_tags
                ), '') AS tags
            FROM assets a
            WHERE {whereClause}
            ORDER BY a.imported_at DESC, a.display_name COLLATE NOCASE;
            """;
    }

    private static async Task<SqliteConnection> OpenConnectionAsync(
        LibraryLocation location,
        CancellationToken cancellationToken)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = location.DatabasePath,
            Pooling = false
        }.ToString();

        var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys = ON;";
        await command.ExecuteNonQueryAsync(cancellationToken);

        return connection;
    }

    private static async Task ExecuteNonQueryAsync(
        SqliteConnection connection,
        string commandText,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = commandText;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string BuildLikeSearchClause()
    {
        return """
            (
                a.display_name LIKE $likeQuery ESCAPE '\'
                OR a.library_relative_path LIKE $likeQuery ESCAPE '\'
                OR a.extension LIKE $likeQuery ESCAPE '\'
                OR a.kind LIKE $likeQuery ESCAPE '\'
                OR a.notes LIKE $likeQuery ESCAPE '\'
                OR EXISTS (
                    SELECT 1
                    FROM asset_tags search_at
                    INNER JOIN tags search_t ON search_t.id = search_at.tag_id
                    WHERE search_at.asset_id = a.id
                    AND search_t.name LIKE $likeQuery ESCAPE '\'
                )
            )
            """;
    }

    private static IReadOnlyList<string> SplitTags(string tags)
    {
        return tags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static string EscapeLike(string value)
    {
        return value
            .Replace(@"\", @"\\", StringComparison.Ordinal)
            .Replace("%", @"\%", StringComparison.Ordinal)
            .Replace("_", @"\_", StringComparison.Ordinal);
    }

    private static string BuildFtsQuery(string query)
    {
        var tokens = SearchTokenRegex()
            .Matches(query)
            .Select(match => match.Value)
            .Where(value => value.Length > 0)
            .Select(value => $"\"{value}\"")
            .ToArray();

        return string.Join(' ', tokens);
    }

    private static string NormalizeTag(string tag)
    {
        return tag.Trim().ToUpperInvariant();
    }

    private static string FormatDate(DateTimeOffset value)
    {
        return value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    }

    private static DateTimeOffset ParseDate(string value)
    {
        return DateTimeOffset.Parse(value, CultureInfo.InvariantCulture);
    }

    [GeneratedRegex(@"[\p{L}\p{N}_]+", RegexOptions.Compiled)]
    private static partial Regex SearchTokenRegex();
}
