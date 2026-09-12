using Microsoft.Data.Sqlite;

namespace Xstar.Music.Core;

public sealed class MusicDatabase : IDisposable
{
    private readonly SqliteConnection _connection;

    public MusicDatabase(string databasePath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        _connection = new SqliteConnection($"Data Source={databasePath}");
        _connection.Open();
        Initialize();
    }

    public void UpsertSongs(IEnumerable<Song> songs)
    {
        using var transaction = _connection.BeginTransaction();
        foreach (var song in songs)
        {
            using var command = _connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO songs (id,path,title,artist,album,format,codec,duration_ms,quality,bitrate,sample_rate,bit_depth,channels,file_size,last_write_ticks,artwork_path)
                VALUES ($id,$path,$title,$artist,$album,$format,$codec,$duration,$quality,$bitrate,$sampleRate,$bitDepth,$channels,$fileSize,$lastWrite,$artwork)
                ON CONFLICT(id) DO UPDATE SET path=excluded.path,title=excluded.title,artist=excluded.artist,album=excluded.album,format=excluded.format,codec=excluded.codec,duration_ms=excluded.duration_ms,quality=excluded.quality,bitrate=excluded.bitrate,sample_rate=excluded.sample_rate,bit_depth=excluded.bit_depth,channels=excluded.channels,file_size=excluded.file_size,last_write_ticks=excluded.last_write_ticks,artwork_path=excluded.artwork_path;
                """;
            Add(command, "$id", song.Id); Add(command, "$path", song.Path); Add(command, "$title", song.Title); Add(command, "$artist", song.Artist); Add(command, "$album", song.Album); Add(command, "$format", song.Format); Add(command, "$codec", song.Codec); Add(command, "$duration", song.DurationMs); Add(command, "$quality", song.Quality); Add(command, "$bitrate", song.Bitrate); Add(command, "$sampleRate", song.SampleRate); Add(command, "$bitDepth", song.BitDepth); Add(command, "$channels", song.Channels); Add(command, "$fileSize", song.FileSize); Add(command, "$lastWrite", song.LastWriteTimeUtcTicks); Add(command, "$artwork", (object?)song.ArtworkPath ?? DBNull.Value);
            command.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    public List<Song> ReadSongs()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT id,path,title,artist,album,format,codec,duration_ms,quality,bitrate,sample_rate,bit_depth,channels,file_size,last_write_ticks,artwork_path FROM songs ORDER BY artist,album,title";
        using var reader = command.ExecuteReader();
        var result = new List<Song>();
        while (reader.Read()) result.Add(new Song { Id = reader.GetString(0), Path = reader.GetString(1), Title = reader.GetString(2), Artist = reader.GetString(3), Album = reader.GetString(4), Format = reader.GetString(5), Codec = reader.GetString(6), DurationMs = reader.GetInt64(7), Quality = reader.GetString(8), Bitrate = reader.GetInt32(9), SampleRate = reader.GetInt32(10), BitDepth = reader.GetInt32(11), Channels = reader.GetInt32(12), FileSize = reader.GetInt64(13), LastWriteTimeUtcTicks = reader.GetInt64(14), ArtworkPath = reader.IsDBNull(15) ? null : reader.GetString(15) });
        return result;
    }

    public void RemoveMissingFiles()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            DELETE FROM songs WHERE NOT EXISTS (SELECT 1 FROM scan_files WHERE scan_files.path = songs.path);
            DELETE FROM favorites WHERE song_id NOT IN (SELECT id FROM songs);
            DELETE FROM play_history WHERE song_id NOT IN (SELECT id FROM songs);
            DELETE FROM playlist_items WHERE song_id NOT IN (SELECT id FROM songs);
            """;
        command.ExecuteNonQuery();
    }

    public HashSet<string> ReadFavoriteIds()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT song_id FROM favorites";
        using var reader = command.ExecuteReader();
        var result = new HashSet<string>(StringComparer.Ordinal);
        while (reader.Read()) result.Add(reader.GetString(0));
        return result;
    }

    public void SetFavorite(string songId, bool favorite)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = favorite
            ? "INSERT OR IGNORE INTO favorites(song_id) VALUES ($id)"
            : "DELETE FROM favorites WHERE song_id = $id";
        command.Parameters.AddWithValue("$id", songId);
        command.ExecuteNonQuery();
    }

    public void AddHistory(string songId, long playedAtTicks)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "INSERT INTO play_history(song_id, played_at_ticks) VALUES ($id, $at)";
        command.Parameters.AddWithValue("$id", songId);
        command.Parameters.AddWithValue("$at", playedAtTicks);
        command.ExecuteNonQuery();
    }

    public Dictionary<string, long> ReadHistory()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT song_id, MAX(played_at_ticks) FROM play_history GROUP BY song_id";
        using var reader = command.ExecuteReader();
        var result = new Dictionary<string, long>(StringComparer.Ordinal);
        while (reader.Read()) result[reader.GetString(0)] = reader.GetInt64(1);
        return result;
    }

    public List<(int Id, string Name)> ReadPlaylists()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT id, name FROM playlists ORDER BY name";
        using var reader = command.ExecuteReader();
        var result = new List<(int Id, string Name)>();
        while (reader.Read()) result.Add((reader.GetInt32(0), reader.GetString(1)));
        return result;
    }

    public int CreatePlaylist(string name)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "INSERT OR IGNORE INTO playlists(name, created_ticks) VALUES ($name, $created); SELECT id FROM playlists WHERE name = $name";
        command.Parameters.AddWithValue("$name", name);
        command.Parameters.AddWithValue("$created", DateTime.UtcNow.Ticks);
        return Convert.ToInt32(command.ExecuteScalar());
    }

    public void DeletePlaylist(int playlistId)
    {
        using var transaction = _connection.BeginTransaction();
        using (var items = _connection.CreateCommand()) { items.Transaction = transaction; items.CommandText = "DELETE FROM playlist_items WHERE playlist_id = $id"; items.Parameters.AddWithValue("$id", playlistId); items.ExecuteNonQuery(); }
        using (var playlist = _connection.CreateCommand()) { playlist.Transaction = transaction; playlist.CommandText = "DELETE FROM playlists WHERE id = $id"; playlist.Parameters.AddWithValue("$id", playlistId); playlist.ExecuteNonQuery(); }
        transaction.Commit();
    }

    public void AddSongToPlaylist(int playlistId, string songId)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "INSERT OR IGNORE INTO playlist_items(playlist_id, song_id, position) VALUES ($pid, $sid, (SELECT COALESCE(MAX(position), -1) + 1 FROM playlist_items WHERE playlist_id = $pid))";
        command.Parameters.AddWithValue("$pid", playlistId);
        command.Parameters.AddWithValue("$sid", songId);
        command.ExecuteNonQuery();
    }

    public void RemoveSongFromPlaylist(int playlistId, string songId)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "DELETE FROM playlist_items WHERE playlist_id = $pid AND song_id = $sid";
        command.Parameters.AddWithValue("$pid", playlistId);
        command.Parameters.AddWithValue("$sid", songId);
        command.ExecuteNonQuery();
    }

    public HashSet<string> ReadPlaylistSongIds(int playlistId)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT song_id FROM playlist_items WHERE playlist_id = $pid";
        command.Parameters.AddWithValue("$pid", playlistId);
        using var reader = command.ExecuteReader();
        var result = new HashSet<string>(StringComparer.Ordinal);
        while (reader.Read()) result.Add(reader.GetString(0));
        return result;
    }

    public void ReplaceScanFiles(IEnumerable<string> paths)
    {
        using var transaction = _connection.BeginTransaction();
        using (var clear = _connection.CreateCommand()) { clear.Transaction = transaction; clear.CommandText = "DELETE FROM scan_files"; clear.ExecuteNonQuery(); }
        foreach (var path in paths) using (var add = _connection.CreateCommand()) { add.Transaction = transaction; add.CommandText = "INSERT OR IGNORE INTO scan_files(path) VALUES ($path)"; add.Parameters.AddWithValue("$path", path); add.ExecuteNonQuery(); }
        transaction.Commit();
    }

    private void Initialize()
    {
        using var command = _connection.CreateCommand();
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
        using var migration = _connection.CreateCommand();
        migration.CommandText = "PRAGMA user_version";
        var version = Convert.ToInt32(migration.ExecuteScalar());
        if (version < 2)
        {
            using var addArtwork = _connection.CreateCommand();
            addArtwork.CommandText = "ALTER TABLE songs ADD COLUMN artwork_path TEXT";
            try { addArtwork.ExecuteNonQuery(); } catch (SqliteException) { }
        }
        if (version < 3)
        {
            using var createMeta = _connection.CreateCommand();
            createMeta.CommandText = """
                CREATE TABLE IF NOT EXISTS favorites (song_id TEXT PRIMARY KEY);
                CREATE TABLE IF NOT EXISTS play_history (id INTEGER PRIMARY KEY AUTOINCREMENT, song_id TEXT NOT NULL, played_at_ticks INTEGER NOT NULL);
                CREATE TABLE IF NOT EXISTS playlists (id INTEGER PRIMARY KEY AUTOINCREMENT, name TEXT NOT NULL UNIQUE, created_ticks INTEGER NOT NULL);
                CREATE TABLE IF NOT EXISTS playlist_items (playlist_id INTEGER NOT NULL, song_id TEXT NOT NULL, position INTEGER NOT NULL, PRIMARY KEY(playlist_id, song_id));
                CREATE INDEX IF NOT EXISTS idx_play_history_song ON play_history(song_id);
                """;
            createMeta.ExecuteNonQuery();
            using var setVersion = _connection.CreateCommand();
            setVersion.CommandText = "PRAGMA user_version = 3";
            setVersion.ExecuteNonQuery();
        }
    }

    private static void Add(SqliteCommand command, string name, object value) => command.Parameters.AddWithValue(name, value);
    public void Dispose() => _connection.Dispose();
}
