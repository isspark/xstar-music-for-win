namespace Xstar.Music.Core;

public sealed class PlaybackQueue
{
    private readonly List<Song> _songs = new();
    public IReadOnlyList<Song> Songs => _songs;
    public int CurrentIndex { get; private set; } = -1;

    public void Replace(IEnumerable<Song> songs, string? currentPath = null)
    {
        _songs.Clear();
        _songs.AddRange(songs);
        CurrentIndex = currentPath is null ? (_songs.Count == 0 ? -1 : 0) : _songs.FindIndex(song => song.Path.Equals(currentPath, StringComparison.OrdinalIgnoreCase));
        if (CurrentIndex < 0 && _songs.Count > 0) CurrentIndex = 0;
    }

    public Song? Current => CurrentIndex >= 0 && CurrentIndex < _songs.Count ? _songs[CurrentIndex] : null;

    public Song? Next(PlaybackMode mode)
    {
        if (_songs.Count == 0) return null;
        if (mode == PlaybackMode.RepeatOne) return Current;
        CurrentIndex = mode == PlaybackMode.Shuffle ? Random.Shared.Next(_songs.Count) : CurrentIndex + 1;
        return CurrentIndex < _songs.Count ? Current : null;
    }
}
