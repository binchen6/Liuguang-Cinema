using System.Collections.Concurrent;
using Microsoft.Data.Sqlite;
using TV.Models;

namespace TV.Services.Persistence;

public sealed class DatabaseService
{
    private readonly string _connectionString = $"Data Source={AppPaths.Database};Pooling=True";
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private readonly ConcurrentDictionary<string, string?> _settings = new(StringComparer.OrdinalIgnoreCase);
    private volatile bool _initialized;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_initialized) return;
        await _initLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_initialized) return;
            AppPaths.EnsureCreated();
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                PRAGMA journal_mode=WAL;
                PRAGMA synchronous=NORMAL;
                PRAGMA cache_size=-8000;
                PRAGMA temp_store=MEMORY;
                CREATE TABLE IF NOT EXISTS settings (key TEXT PRIMARY KEY, value TEXT NOT NULL);
                CREATE TABLE IF NOT EXISTS history (
                    id TEXT PRIMARY KEY, title TEXT NOT NULL, picture TEXT NOT NULL, kind TEXT NOT NULL,
                    subtitle TEXT NOT NULL, position INTEGER NOT NULL, duration INTEGER NOT NULL,
                    updated INTEGER NOT NULL, request TEXT NOT NULL);
                CREATE INDEX IF NOT EXISTS idx_history_updated ON history(updated DESC);
                CREATE TABLE IF NOT EXISTS favorites (
                    id TEXT PRIMARY KEY, title TEXT NOT NULL, picture TEXT NOT NULL, kind TEXT NOT NULL,
                    subtitle TEXT NOT NULL, added INTEGER NOT NULL, data TEXT NOT NULL);
                CREATE INDEX IF NOT EXISTS idx_favorites_added ON favorites(added DESC);
                CREATE TABLE IF NOT EXISTS metadata (
                    key TEXT PRIMARY KEY, json TEXT NOT NULL, expires INTEGER NOT NULL);
                CREATE TABLE IF NOT EXISTS downloads (
                    path TEXT PRIMARY KEY COLLATE NOCASE, title TEXT NOT NULL, episode TEXT NOT NULL, added INTEGER NOT NULL);
                CREATE TABLE IF NOT EXISTS local_files (
                    path TEXT PRIMARY KEY COLLATE NOCASE, title TEXT NOT NULL, folder TEXT NOT NULL,
                    size INTEGER NOT NULL, modified INTEGER NOT NULL, added INTEGER NOT NULL);
                CREATE INDEX IF NOT EXISTS idx_local_files_folder ON local_files(folder);
                """;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            await using var read = connection.CreateCommand();
            read.CommandText = "SELECT key,value FROM settings";
            await using var reader = await read.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) _settings[reader.GetString(0)] = reader.GetString(1);
            _initialized = true;
        }
        finally { _initLock.Release(); }
    }

    public Task<string?> GetSettingAsync(string key, CancellationToken cancellationToken = default)
    {
        if (_initialized) return Task.FromResult(_settings.GetValueOrDefault(key));
        return GetSettingSlowAsync(key, cancellationToken);
    }

    public string? TryGetCachedSetting(string key) => _initialized ? _settings.GetValueOrDefault(key) : null;

    private async Task<string?> GetSettingSlowAsync(string key, CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        return _settings.GetValueOrDefault(key);
    }

    public async Task SetSettingAsync(string key, string value, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO settings(key,value) VALUES($key,$value) ON CONFLICT(key) DO UPDATE SET value=excluded.value";
            command.Parameters.AddWithValue("$key", key);
            command.Parameters.AddWithValue("$value", value);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            _settings[key] = value;
        }
        finally { _writeLock.Release(); }
    }

    internal async Task<(string? First, string? Second)> GetSettingPairAsync(string firstKey, string secondKey, CancellationToken token)
    {
        await InitializeAsync(token).ConfigureAwait(false);
        await _writeLock.WaitAsync(token).ConfigureAwait(false);
        try { return (_settings.GetValueOrDefault(firstKey), _settings.GetValueOrDefault(secondKey)); }
        finally { _writeLock.Release(); }
    }

    internal async Task SetSettingPairAsync((string Key, string Value) first, (string Key, string Value) second, CancellationToken token)
    {
        await InitializeAsync(token).ConfigureAwait(false);
        await _writeLock.WaitAsync(token).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenAsync(token).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO settings(key,value) VALUES($firstKey,$firstValue),($secondKey,$secondValue)
                ON CONFLICT(key) DO UPDATE SET value=excluded.value
                """;
            command.Parameters.AddWithValue("$firstKey", first.Key);
            command.Parameters.AddWithValue("$firstValue", first.Value);
            command.Parameters.AddWithValue("$secondKey", second.Key);
            command.Parameters.AddWithValue("$secondValue", second.Value);
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            _settings[first.Key] = first.Value;
            _settings[second.Key] = second.Value;
        }
        finally { _writeLock.Release(); }
    }

    public async Task<List<HistoryEntry>> GetHistoryAsync(CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id,title,picture,kind,subtitle,position,duration,updated,request FROM history ORDER BY updated DESC LIMIT 200";
        var result = new List<HistoryEntry>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            result.Add(ReadHistoryEntry(reader));
        return result;
    }

    public async Task<HistoryEntry?> GetHistoryEntryAsync(string id, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id,title,picture,kind,subtitle,position,duration,updated,request FROM history WHERE id=$id LIMIT 1";
        command.Parameters.AddWithValue("$id", id);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadHistoryEntry(reader) : null;
    }

    private static HistoryEntry ReadHistoryEntry(SqliteDataReader reader) => new()
    {
        Id = reader.GetString(0),
        Title = reader.GetString(1),
        Picture = reader.GetString(2),
        Kind = reader.GetString(3),
        Subtitle = reader.GetString(4),
        PositionMs = reader.GetInt64(5),
        DurationMs = reader.GetInt64(6),
        UpdatedAt = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(7)),
        RequestJson = reader.GetString(8)
    };

    public async Task SaveHistoryAsync(PlaybackRequest request, long positionMs, long durationMs, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO history(id,title,picture,kind,subtitle,position,duration,updated,request)
                VALUES($id,$title,$picture,$kind,$subtitle,$position,$duration,$updated,$request)
                ON CONFLICT(id) DO UPDATE SET title=excluded.title,subtitle=excluded.subtitle,position=excluded.position,
                    duration=excluded.duration,updated=excluded.updated,request=excluded.request,picture=excluded.picture;
                DELETE FROM history WHERE id NOT IN (SELECT id FROM history ORDER BY updated DESC LIMIT 200);
                """;
            command.Parameters.AddWithValue("$id", GetRequestId(request));
            command.Parameters.AddWithValue("$title", request.Title);
            command.Parameters.AddWithValue("$picture", request.Picture ?? request.Item?.BestPicture ?? string.Empty);
            command.Parameters.AddWithValue("$kind", request.Kind.ToString());
            command.Parameters.AddWithValue("$subtitle", request.Subtitle);
            command.Parameters.AddWithValue("$position", positionMs);
            command.Parameters.AddWithValue("$duration", durationMs);
            command.Parameters.AddWithValue("$updated", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            command.Parameters.AddWithValue("$request", System.Text.Json.JsonSerializer.Serialize(request, JsonUtil.Options));
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally { _writeLock.Release(); }
    }

    public async Task<List<DownloadEntry>> GetDownloadsAsync(CancellationToken token = default)
    {
        await InitializeAsync(token).ConfigureAwait(false);
        await using var connection = await OpenAsync(token).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT path,title,episode FROM downloads ORDER BY added DESC";
        var result = new List<DownloadEntry>();
        await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        while (await reader.ReadAsync(token).ConfigureAwait(false))
            result.Add(new DownloadEntry(reader.GetString(0), reader.GetString(1), reader.GetString(2)));
        return result;
    }

    public Task SaveDownloadAsync(DownloadEntry entry, CancellationToken token = default) =>
        WriteDownloadAsync(entry, false, token);

    public Task RemoveDownloadAsync(DownloadEntry entry, CancellationToken token = default) =>
        WriteDownloadAsync(entry, true, token);

    private async Task WriteDownloadAsync(DownloadEntry entry, bool remove, CancellationToken token)
    {
        await InitializeAsync(token).ConfigureAwait(false);
        await _writeLock.WaitAsync(token).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenAsync(token).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = remove ? "DELETE FROM downloads WHERE path=$path"
                : "INSERT INTO downloads VALUES($path,$title,$episode,$added) ON CONFLICT(path) DO UPDATE SET title=excluded.title,episode=excluded.episode,added=excluded.added";
            command.Parameters.AddWithValue("$path", entry.FilePath);
            if (!remove)
            {
                command.Parameters.AddWithValue("$title", entry.Title);
                command.Parameters.AddWithValue("$episode", entry.Episode);
                command.Parameters.AddWithValue("$added", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            }
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }
        finally { _writeLock.Release(); }
    }

    public async Task<List<LocalMediaEntry>> GetLocalFilesAsync(CancellationToken token = default)
    {
        await InitializeAsync(token).ConfigureAwait(false);
        await using var connection = await OpenAsync(token).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT path,title,folder,size,modified FROM local_files ORDER BY added DESC LIMIT 5000";
        var result = new List<LocalMediaEntry>();
        await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        while (await reader.ReadAsync(token).ConfigureAwait(false))
            result.Add(new LocalMediaEntry(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt64(3),
                DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(4))));
        return result;
    }

    public async Task SaveLocalFilesAsync(IReadOnlyList<LocalMediaEntry> files, string? folder, CancellationToken token = default)
    {
        await InitializeAsync(token).ConfigureAwait(false);
        await _writeLock.WaitAsync(token).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenAsync(token).ConfigureAwait(false);
            using var transaction = connection.BeginTransaction();
            if (folder is not null)
            {
                await using var delete = connection.CreateCommand();
                delete.Transaction = transaction;
                delete.CommandText = "DELETE FROM local_files WHERE folder=$folder";
                delete.Parameters.AddWithValue("$folder", folder);
                await delete.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            }
            if (files.Count > 0)
            {
                await using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = """
                    INSERT INTO local_files(path,title,folder,size,modified,added) VALUES($path,$title,$folder,$size,$modified,$added)
                    ON CONFLICT(path) DO UPDATE SET title=excluded.title,folder=excluded.folder,size=excluded.size,modified=excluded.modified,added=excluded.added;
                    """;
                var pPath = command.Parameters.Add("$path", SqliteType.Text);
                var pTitle = command.Parameters.Add("$title", SqliteType.Text);
                var pFolder = command.Parameters.Add("$folder", SqliteType.Text);
                var pSize = command.Parameters.Add("$size", SqliteType.Integer);
                var pModified = command.Parameters.Add("$modified", SqliteType.Integer);
                var pAdded = command.Parameters.Add("$added", SqliteType.Integer);
                command.Prepare();

                var addedNow = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                foreach (var file in files)
                {
                    pPath.Value = file.Path;
                    pTitle.Value = file.Title;
                    pFolder.Value = file.Folder;
                    pSize.Value = file.Size;
                    pModified.Value = file.ModifiedAt.ToUnixTimeMilliseconds();
                    pAdded.Value = addedNow;
                    await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }
            }
            transaction.Commit();
        }
        finally { _writeLock.Release(); }
    }

    public async Task RemoveLocalFileAsync(string path, string? folder = null, CancellationToken token = default)
    {
        await InitializeAsync(token).ConfigureAwait(false);
        await _writeLock.WaitAsync(token).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenAsync(token).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = folder is null ? "DELETE FROM local_files WHERE path=$path" : "DELETE FROM local_files WHERE folder=$folder";
            command.Parameters.AddWithValue("$path", path);
            if (folder is not null) command.Parameters.AddWithValue("$folder", folder);
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }
        finally { _writeLock.Release(); }
    }

    public async Task DeleteHistoryAsync(string id, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM history WHERE id=$id";
            command.Parameters.AddWithValue("$id", id);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally { _writeLock.Release(); }
    }

    public async Task<List<FavoriteEntry>> GetFavoritesAsync(CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id,title,picture,kind,subtitle,added,data FROM favorites ORDER BY added DESC LIMIT 200";
        var result = new List<FavoriteEntry>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(new FavoriteEntry
            {
                Id = reader.GetString(0), Title = reader.GetString(1), Picture = reader.GetString(2),
                Kind = reader.GetString(3), Subtitle = reader.GetString(4),
                AddedAt = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(5)), DataJson = reader.GetString(6)
            });
        }
        return result;
    }

    public async Task<bool> IsFavoriteAsync(string id, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM favorites WHERE id=$id)";
        command.Parameters.AddWithValue("$id", id);
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture) == 1;
    }

    public async Task SetFavoriteAsync(string id, string title, string picture, string kind, string subtitle, string dataJson, bool favorite, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = favorite
                ? "INSERT INTO favorites VALUES($id,$title,$picture,$kind,$subtitle,$added,$data) ON CONFLICT(id) DO UPDATE SET title=excluded.title,picture=excluded.picture,kind=excluded.kind,subtitle=excluded.subtitle,data=excluded.data"
                : "DELETE FROM favorites WHERE id=$id";
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$title", title);
            command.Parameters.AddWithValue("$picture", picture);
            command.Parameters.AddWithValue("$kind", kind);
            command.Parameters.AddWithValue("$subtitle", subtitle);
            command.Parameters.AddWithValue("$added", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            command.Parameters.AddWithValue("$data", dataJson);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally { _writeLock.Release(); }
    }

    public async Task<MetadataCacheEntry?> GetMetadataAsync(string key, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT json,expires FROM metadata WHERE key=$key";
        command.Parameters.AddWithValue("$key", key);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) return null;
        return new MetadataCacheEntry { CacheKey = key, Json = reader.GetString(0), ExpiresAt = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(1)) };
    }

    public async Task SaveMetadataAsync(string key, string json, DateTimeOffset expiresAt, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO metadata(key,json,expires) VALUES($key,$json,$expires) ON CONFLICT(key) DO UPDATE SET json=excluded.json,expires=excluded.expires";
            command.Parameters.AddWithValue("$key", key);
            command.Parameters.AddWithValue("$json", json);
            command.Parameters.AddWithValue("$expires", expiresAt.ToUnixTimeMilliseconds());
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally { _writeLock.Release(); }
    }

    public async Task ClearTableAsync(string table, CancellationToken cancellationToken = default)
    {
        if (table is not ("history" or "favorites" or "metadata")) throw new ArgumentOutOfRangeException(nameof(table));
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = $"DELETE FROM {table}";
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally { _writeLock.Release(); }
    }

    public async Task<int> CountRowsAsync(string table, CancellationToken cancellationToken = default)
    {
        if (table is not ("history" or "favorites" or "metadata" or "downloads")) throw new ArgumentOutOfRangeException(nameof(table));
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {table}";
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture);
    }

    public static string GetVodId(string siteKey, string vodId) => $"vod:{siteKey}:{vodId}";
    public static string GetLiveId(string url) => $"live:{url}";
    public static string GetRequestId(PlaybackRequest request) => request.Kind == PlaybackKind.Vod
        ? GetVodId(request.SiteKey, request.VodId)
        : GetLiveId(request.Url);

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var timeout = connection.CreateCommand();
            timeout.CommandText = "PRAGMA busy_timeout=3000;";
            await timeout.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }
}
