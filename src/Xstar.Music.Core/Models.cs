namespace Xstar.Music.Core;

public sealed class Song
{
    public string Id { get; init; } = "";
    public string Path { get; init; } = "";
    public string Title { get; init; } = "未知歌曲";
    public string Artist { get; init; } = "未知艺术家";
    public string Album { get; init; } = "未知专辑";
    public string Format { get; init; } = "";
    public long DurationMs { get; init; }
    public string Quality { get; init; } = "普通";
    public string? ArtworkPath { get; init; }
    public string Codec { get; init; } = "";
    public int Bitrate { get; init; }
    public int SampleRate { get; init; }
    public int BitDepth { get; init; }
    public int Channels { get; init; }
    public long FileSize { get; init; }
    public long LastWriteTimeUtcTicks { get; init; }
}

public sealed record LyricLine(long TimeMs, string Text);

public enum PlaybackMode
{
    Sequential,
    Shuffle,
    RepeatOne
}
