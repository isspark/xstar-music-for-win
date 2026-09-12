namespace Xstar.Music.Core;

public sealed record PlayerSnapshot(
    string? SongId,
    bool IsPlaying,
    TimeSpan Position,
    TimeSpan Duration,
    PlaybackMode Mode);

public interface IPlayerEngine : IDisposable
{
    string Name { get; }
    PlayerSnapshot Snapshot { get; }
    event EventHandler? SnapshotChanged;
    event EventHandler? Opened;
    event EventHandler? Ended;
    event EventHandler<string>? Failed;
    void Load(Song song);
    void Play();
    void Pause();
    void Stop();
    void Seek(TimeSpan position);
    void SetVolume(double volume);
    void SetMode(PlaybackMode mode);
}
