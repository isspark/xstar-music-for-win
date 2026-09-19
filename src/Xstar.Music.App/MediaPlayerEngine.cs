using System.Windows.Media;
using Xstar.Music.Core;

namespace Xstar.Music.App;

public sealed class MediaPlayerEngine : IPlayerEngine
{
    private readonly MediaPlayer _player = new();
    private Song? _song;
    private bool _isPlaying;
    private PlaybackMode _mode = PlaybackMode.Sequential;

    public MediaPlayerEngine()
    {
        _player.MediaOpened += (_, _) => Opened?.Invoke(this, EventArgs.Empty);
        _player.MediaEnded += (_, _) => { _isPlaying = false; Ended?.Invoke(this, EventArgs.Empty); };
        _player.MediaFailed += (_, e) => { _isPlaying = false; Failed?.Invoke(this, e.ErrorException?.Message ?? "未知错误"); };
    }

    public string Name => "MediaPlayer";

    public PlayerSnapshot Snapshot => new(
        _song?.Id,
        _isPlaying,
        _player.Position,
        _player.NaturalDuration.HasTimeSpan ? _player.NaturalDuration.TimeSpan : TimeSpan.Zero,
        _mode);

    public event EventHandler? SnapshotChanged;
    public event EventHandler? Opened;
    public event EventHandler? Ended;
    public event EventHandler<string>? Failed;

    public bool Load(Song song)
    {
        _song = song;
        _player.Open(new Uri(song.Path));
        SnapshotChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public void Play()
    {
        _player.Play();
        _isPlaying = true;
        SnapshotChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Pause()
    {
        _player.Pause();
        _isPlaying = false;
        SnapshotChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Stop()
    {
        _player.Stop();
        _isPlaying = false;
        SnapshotChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Seek(TimeSpan position) => _player.Position = position;

    public void SetVolume(double volume) => _player.Volume = volume;

    public void SetMode(PlaybackMode mode)
    {
        _mode = mode;
        SnapshotChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose() => _player.Close();
}
