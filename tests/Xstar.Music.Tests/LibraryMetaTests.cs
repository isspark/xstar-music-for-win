using Xstar.Music.Core;

namespace Xstar.Music.Tests;

public sealed class LibraryMetaTests : IDisposable
{
    private readonly string _root;
    private readonly string _databasePath;

    public LibraryMetaTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "xstar-meta-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _databasePath = Path.Combine(_root, "library.db");
    }

    [Fact]
    public void FavoritesRoundTrip()
    {
        using var database = new MusicDatabase(_databasePath);
        Assert.Empty(database.ReadFavoriteIds());

        database.SetFavorite("song-a", true);
        database.SetFavorite("song-b", true);
        database.SetFavorite("song-a", false);

        var favorites = database.ReadFavoriteIds();
        Assert.Single(favorites);
        Assert.Contains("song-b", favorites);
    }

    [Fact]
    public void HistoryKeepsLatestPlaybackPerSong()
    {
        using var database = new MusicDatabase(_databasePath);
        database.AddHistory("song-a", 100);
        database.AddHistory("song-a", 300);
        database.AddHistory("song-b", 200);

        var history = database.ReadHistory();
        Assert.Equal(2, history.Count);
        Assert.Equal(300, history["song-a"]);
        Assert.Equal(200, history["song-b"]);
    }

    [Fact]
    public void PlaylistAddRemoveAndDelete()
    {
        using var database = new MusicDatabase(_databasePath);
        var id = database.CreatePlaylist("晚间");
        Assert.True(id > 0);
        Assert.Equal(id, database.CreatePlaylist("晚间"));

        database.AddSongToPlaylist(id, "song-a");
        database.AddSongToPlaylist(id, "song-b");
        database.AddSongToPlaylist(id, "song-a");

        var songs = database.ReadPlaylistSongIds(id);
        Assert.Equal(2, songs.Count);

        database.RemoveSongFromPlaylist(id, "song-a");
        Assert.Single(database.ReadPlaylistSongIds(id));

        database.DeletePlaylist(id);
        Assert.Empty(database.ReadPlaylists());
        Assert.Empty(database.ReadPlaylistSongIds(id));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }
}
