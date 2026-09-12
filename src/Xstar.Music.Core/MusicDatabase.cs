using Microsoft.Data.Sqlite;

namespace Xstar.Music.Core;

public sealed class MusicDatabase : IDisposable
{
    private readonly string _connectionString;
    private readonly string _dbPath;
    private static readonly object _dbLock = new();

    public MusicDatabase(string databasePath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        _dbPath = databasePath;
        _connectionString = $"Data Source=\"{databasePath}\"";
        // Initialize schema once on construction, with retry because orphan
        // tray processes may briefly hold the DB lock (Error 10/5).
        try
        {
            InitializeWithRetry();
        }
        catch (SqliteException se) when (se.SqliteErrorCode is 10 or 11)
        {
            // Physically corrupt db file (10=disk I/O during DDL, 11=malformed image)
            // — typically from an unclean prior shutdown. Self-heal: back up the
            // corrupt file, delete aux files, then rebuild a fresh empty schema so the
            // app can start. (Error 14 CANTOPEN is intentionally excluded: it most often
            // means a lock, and we must not delete the DB when another process holds it.)
            // The app runs as the user and is allowed to touch its own AppData files
            // even though the agent sandbox isn't.
            TryBackupCorrupt();
            GC.Collect();
            GC.WaitForPendingFinalizers();
            Thread.Sleep(100);
            EnsureAuxDeleted();
            InitializeWithRetry();
        }
    }

    private void TryBackupCorrupt()
    {
        try
        {
            var bak = $"{_dbPath}.corrupt_{DateTime.Now:yyyyMMdd_HHmmss}.bak";
            if (File.Exists(_dbPath)) File.Move(_dbPath, bak);
        }
        catch { /* keep going — deletion path below is the important one */ }
    }

    private void EnsureAuxDeleted()
    {
        foreach (var f in new[] { _dbPath + "-journal", _dbPath + "-wal", _dbPath + "-shm" })
        {
            try { if (File.Exists(f)) File.Delete(f); } catch { }
        }
    }

    private void InitializeWithRetry()
    {
        var lastEx = default(Exception);
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                using var conn = NewConnection();
                Initialize(conn);
                return;
            }
            catch (Exception ex)
            {
                lastEx = ex;
                Thread.Sleep(100 * (attempt + 1));
            }
        }
        throw lastEx!;
    }

    private SqliteConnection NewConnection()
    {
        var lastEx = default(Exception);
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                var conn = new SqliteConnection(_connectionString);
                conn.Open();
                using (var p = conn.CreateCommand())
                {
                    p.CommandText = "PRAGMA busy_timeout=8000";
                    p.ExecuteNonQuery();
                }
                using (var p = conn.CreateCommand())
                {
                    p.CommandText = "PRAGMA synchronous=NORMAL";
                    p.ExecuteNonQuery();
                }
                // WAL mode — best effort. If WAL can't be set (another process holds
                // a DELETE-mode connection, or the file is dirty from a prior kill),
                // we gracefully fall back to DELETE mode instead of failing startup.
                try
                {
                    for (var walAttempt = 0; walAttempt < 3; walAttempt++)
                    {
                        using var wp = conn.CreateCommand();
                        wp.CommandText = "PRAGMA journal_mode=WAL";
                        var mode = wp.ExecuteScalar() as string;
                        if (mode == "wal") break;
                        Thread.Sleep(30);
                    }
                }
                catch { /* WAL best-effort only — not fatal */ }
                return conn;
            }
            catch (Exception ex)
            {
                lastEx = ex;
                Thread.Sleep(100 * (attempt + 1));
            }
        }
        throw lastEx!;
    }

    public void UpsertSongs(IEnumerable<Song> songs)
    {
        lock (_dbLock)
        {
            using var conn = NewConnection();
            using var transaction = conn.BeginTransaction();
            using var command = conn.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO songs (id,path,title,artist,album,format,codec,duration_ms,quality,bitrate,sample_rate,bit_depth,channels,file_size,last_write_ticks,artwork_path)
                VALUES ($id,$path,$title,$artist,$album,$format,$codec,$duration,$quality,$bitrate,$sampleRate,$bitDepth,$channels,$fileSize,$lastWrite,$artwork)
                ON CONFLICT(id) DO UPDATE SET path=excluded.path,title=excluded.title,artist=excluded.artist,album=excluded.album,format=excluded.format,codec=excluded.codec,duration_ms=excluded.duration_ms,quality=excluded.quality,bitrate=excluded.bitrate,sample_rate=excluded.sample_rate,bit_depth=excluded.bit_depth,channels=excluded.channels,file_size=excluded.file_size,last_write_ticks=excluded.last_write_ticks,artwork_path=excluded.artwork_path;
                """;
            Add(command, "$id", string.Empty); Add(command, "$path", string.Empty); Add(command, "$title", string.Empty); Add(command, "$artist", string.Empty); Add(command, "$album", string.Empty); Add(command, "$format", string.Empty); Add(command, "$codec", string.Empty); Add(command, "$duration", 0L); Add(command, "$quality", string.Empty); Add(command, "$bitrate", 0); Add(command, "$sampleRate", 0); Add(command, "$bitDepth", 0); Add(command, "$channels", 0); Add(command, "$fileSize", 0L); Add(command, "$lastWrite", 0L); Add(command, "$artwork", (object?)DBNull.Value);
            command.Prepare();
            foreach (var song in songs)
            {
                command.Parameters["$id"].Value = song.Id;
                command.Parameters["$path"].Value = song.Path;
                command.Parameters["$title"].Value = song.Title;
                command.Parameters["$artist"].Value = song.Artist;
                command.Parameters["$album"].Value = song.Album;
                command.Parameters["$format"].Value = song.Format;
                command.Parameters["$codec"].Value = song.Codec;
                command.Parameters["$duration"].Value = song.DurationMs;
                command.Parameters["$quality"].Value = song.Quality;
                command.Parameters["$bitrate"].Value = song.Bitrate;
                command.Parameters["$sampleRate"].Value = song.SampleRate;
                command.Parameters["$bitDepth"].Value = song.BitDepth;
                command.Parameters["$channels"].Value = song.Channels;
                command.Parameters["$fileSize"].Value = song.FileSize;
                command.Parameters["$lastWrite"].Value = song.LastWriteTimeUtcTicks;
                command.Parameters["$artwork"].Value = (object?)song.ArtworkPath ?? DBNull.Value;
                command.ExecuteNonQuery();
            }
            transaction.Commit();
        }
    }

    public List<Song> ReadSongs()
    {
        lock (_dbLock)
        {
            using var conn = NewConnection();
            using var command = conn.CreateCommand();
            command.CommandText = "SELECT id,path,title,artist,album,format,codec,duration_ms,quality,bitrate,sample_rate,bit_depth,channels,file_size,last_write_ticks,artwork_path FROM songs ORDER BY artist,album,title";
            using var reader = command.ExecuteReader();
            var result = new List<Song>();
            while (reader.Read()) result.Add(new Song { Id = reader.GetString(0), Path = reader.GetString(1), Title = reader.GetString(2), Artist = reader.GetString(3), Album = reader.GetString(4), Format = reader.GetString(5), Codec = reader.GetString(6), DurationMs = reader.GetInt64(7), Quality = reader.GetString(8), Bitrate = reader.GetInt32(9), SampleRate = reader.GetInt32(10), BitDepth = reader.GetInt32(11), Channels = reader.GetInt32(12), FileSize = reader.GetInt64(13), LastWriteTimeUtcTicks = reader.GetInt64(14), ArtworkPath = reader.IsDBNull(15) ? null : reader.GetString(15) });
            return result;
        }
    }

    public void RemoveMissingFiles()
    {
        lock (_dbLock)
        {
            using var conn = NewConnection();
            using var command = conn.CreateCommand();
            command.CommandText = """
                DELETE FROM songs WHERE NOT EXISTS (SELECT 1 FROM scan_files WHERE scan_files.path = songs.path);
                DELETE FROM favorites WHERE song_id NOT IN (SELECT id FROM songs);
                DELETE FROM play_history WHERE song_id NOT IN (SELECT id FROM songs);
                DELETE FROM playlist_items WHERE song_id NOT IN (SELECT id FROM songs);
                """;
            command.ExecuteNonQuery();
        }
    }

    public HashSet<string> ReadFavoriteIds()
    {
        lock (_dbLock)
        {
            using var conn = NewConnection();
            using var command = conn.CreateCommand();
            command.CommandText = "SELECT song_id FROM favorites";
            using var reader = command.ExecuteReader();
            var result = new HashSet<string>(StringComparer.Ordinal);
            while (reader.Read()) result.Add(reader.GetString(0));
            return result;
        }
    }

    public void SetFavorite(string songId, bool favorite)
    {
        lock (_dbLock)
        {
            using var conn = NewConnection();
            using var command = conn.CreateCommand();
            command.CommandText = favorite
                ? "INSERT OR IGNORE INTO favorites(song_id) VALUES ($id)"
                : "DELETE FROM favorites WHERE song_id = $id";
            command.Parameters.AddWithValue("$id", songId);
            command.ExecuteNonQuery();
        }
    }

    public void AddHistory(string songId, long playedAtTicks)
    {
        lock (_dbLock)
        {
            using var conn = NewConnection();
            using var command = conn.CreateCommand();
            command.CommandText = "INSERT INTO play_history(song_id, played_at_ticks) VALUES ($id, $at)";
            command.Parameters.AddWithValue("$id", songId);
            command.Parameters.AddWithValue("$at", playedAtTicks);
            command.ExecuteNonQuery();
        }
    }

    public Dictionary<string, long> ReadHistory()
    {
        lock (_dbLock)
        {
            using var conn = NewConnection();
            using var command = conn.CreateCommand();
            command.CommandText = "SELECT song_id, MAX(played_at_ticks) FROM play_history GROUP BY song_id";
            using var reader = command.ExecuteReader();
            var result = new Dictionary<string, long>(StringComparer.Ordinal);
            while (reader.Read()) result[reader.GetString(0)] = reader.GetInt64(1);
            return result;
        }
    }

    public List<(int Id, string Name)> ReadPlaylists()
    {
        lock (_dbLock)
        {
            using var conn = NewConnection();
            using var command = conn.CreateCommand();
            command.CommandText = "SELECT id, name FROM playlists ORDER BY name";
            using var reader = command.ExecuteReader();
            var result = new List<(int Id, string Name)>();
            while (reader.Read()) result.Add((reader.GetInt32(0), reader.GetString(1)));
            return result;
        }
    }

    public int CreatePlaylist(string name)
    {
        lock (_dbLock)
        {
            using var conn = NewConnection();
            using var command = conn.CreateCommand();
            command.CommandText = "INSERT OR IGNORE INTO playlists(name, created_ticks) VALUES ($name, $created); SELECT id FROM playlists WHERE name = $name";
            command.Parameters.AddWithValue("$name", name);
            command.Parameters.AddWithValue("$created", DateTime.UtcNow.Ticks);
            return Convert.ToInt32(command.ExecuteScalar());
        }
    }

    public void DeletePlaylist(int playlistId)
    {
        lock (_dbLock)
        {
            using var conn = NewConnection();
            using var transaction = conn.BeginTransaction();
            using (var items = conn.CreateCommand()) { items.Transaction = transaction; items.CommandText = "DELETE FROM playlist_items WHERE playlist_id = $id"; items.Parameters.AddWithValue("$id", playlistId); items.ExecuteNonQuery(); }
            using (var playlist = conn.CreateCommand()) { playlist.Transaction = transaction; playlist.CommandText = "DELETE FROM playlists WHERE id = $id"; playlist.Parameters.AddWithValue("$id", playlistId); playlist.ExecuteNonQuery(); }
            transaction.Commit();
        }
    }

    public void AddSongToPlaylist(int playlistId, string songId)
    {
        lock (_dbLock)
        {
            using var conn = NewConnection();
            using var command = conn.CreateCommand();
            command.CommandText = "INSERT OR IGNORE INTO playlist_items(playlist_id, song_id, position) VALUES ($pid, $sid, (SELECT COALESCE(MAX(position), -1) + 1 FROM playlist_items WHERE playlist_id = $pid))";
            command.Parameters.AddWithValue("$pid", playlistId);
            command.Parameters.AddWithValue("$sid", songId);
            command.ExecuteNonQuery();
        }
    }

    public void RemoveSongFromPlaylist(int playlistId, string songId)
    {
        lock (_dbLock)
        {
            using var conn = NewConnection();
            using var command = conn.CreateCommand();
            command.CommandText = "DELETE FROM playlist_items WHERE playlist_id = $pid AND song_id = $sid";
            command.Parameters.AddWithValue("$pid", playlistId);
            command.Parameters.AddWithValue("$sid", songId);
            command.ExecuteNonQuery();
        }
    }

    public HashSet<string> ReadPlaylistSongIds(int playlistId)
    {
        lock (_dbLock)
        {
            using var conn = NewConnection();
            using var command = conn.CreateCommand();
            command.CommandText = "SELECT song_id FROM playlist_items WHERE playlist_id = $pid";
            command.Parameters.AddWithValue("$pid", playlistId);
            using var reader = command.ExecuteReader();
            var result = new HashSet<string>(StringComparer.Ordinal);
            while (reader.Read()) result.Add(reader.GetString(0));
            return result;
        }
    }

    public void ReplaceScanFiles(IEnumerable<string> paths)
    {
        lock (_dbLock)
        {
            using var conn = NewConnection();
            using var transaction = conn.BeginTransaction();
            using (var clear = conn.CreateCommand()) { clear.Transaction = transaction; clear.CommandText = "DELETE FROM scan_files"; clear.ExecuteNonQuery(); }
            using var add = conn.CreateCommand();
            add.Transaction = transaction;
            add.CommandText = "INSERT OR IGNORE INTO scan_files(path) VALUES ($path)";
            add.Parameters.AddWithValue("$path", string.Empty);
            add.Prepare();
            foreach (var path in paths)
            {
                add.Parameters["$path"].Value = path;
                add.ExecuteNonQuery();
            }
            transaction.Commit();
        }
    }

    private static void Initialize(SqliteConnection conn)
    {
        using var command = conn.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS songs (id TEXT PRIMARY KEY,path TEXT NOT NULL UNIQUE,title TEXT NOT NULL,artist TEXT NOT NULL,album TEXT NOT NULL,format TEXT NOT NULL,codec TEXT NOT NULL,duration_ms INTEGER NOT NULL,quality TEXT NOT NULL,bitrate INTEGER NOT NULL,sample_rate INTEGER NOT NULL,bit_depth INTEGER NOT NULL,channels INTEGER NOT NULL,file_size INTEGER NOT NULL,last_write_ticks INTEGER NOT NULL,artwork_path TEXT);
            CREATE TABLE IF NOT EXISTS scan_files (path TEXT PRIMARY KEY);
            CREATE TABLE IF NOT EXISTS favorites (song_id TEXT PRIMARY KEY);
            CREATE TABLE IF NOT EXISTS play_history (id INTEGER PRIMARY KEY AUTOINCREMENT, song_id TEXT NOT NULL, played_at_ticks INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS playlists (id INTEGER PRIMARY KEY AUTOINCREMENT, name TEXT NOT NULL UNIQUE, created_ticks INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS playlist_items (playlist_id INTEGER NOT NULL, song_id TEXT NOT NULL, position INTEGER NOT NULL, PRIMARY KEY(playlist_id, song_id));
            CREATE INDEX IF NOT EXISTS idx_play_history_song ON play_history(song_id);
            """;
        command.ExecuteNonQuery();
        using var migration = conn.CreateCommand();
        migration.CommandText = "PRAGMA user_version";
        var version = Convert.ToInt32(migration.ExecuteScalar());
        if (version < 2)
        {
            using var addArtwork = conn.CreateCommand();
            addArtwork.CommandText = "ALTER TABLE songs ADD COLUMN artwork_path TEXT";
            try { addArtwork.ExecuteNonQuery(); } catch (SqliteException) { }
        }
        if (version < 3)
        {
            using var createMeta = conn.CreateCommand();
            createMeta.CommandText = """
                CREATE TABLE IF NOT EXISTS favorites (song_id TEXT PRIMARY KEY);
                CREATE TABLE IF NOT EXISTS play_history (id INTEGER PRIMARY KEY AUTOINCREMENT, song_id TEXT NOT NULL, played_at_ticks INTEGER NOT NULL);
                CREATE TABLE IF NOT EXISTS playlists (id INTEGER PRIMARY KEY AUTOINCREMENT, name TEXT NOT NULL UNIQUE, created_ticks INTEGER NOT NULL);
                CREATE TABLE IF NOT EXISTS playlist_items (playlist_id INTEGER NOT NULL, song_id TEXT NOT NULL, position INTEGER NOT NULL, PRIMARY KEY(playlist_id, song_id));
                CREATE INDEX IF NOT EXISTS idx_play_history_song ON play_history(song_id);
                """;
            createMeta.ExecuteNonQuery();
            using var setVersion = conn.CreateCommand();
            setVersion.CommandText = "PRAGMA user_version = 3";
            setVersion.ExecuteNonQuery();
        }
    }

    private static void Add(SqliteCommand command, string name, object value) => command.Parameters.AddWithValue(name, value);

    public void Dispose() { }
}
