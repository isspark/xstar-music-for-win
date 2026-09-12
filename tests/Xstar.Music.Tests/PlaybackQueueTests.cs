using Xstar.Music.Core;

namespace Xstar.Music.Tests;

public sealed class PlaybackQueueTests
{
    [Fact]
    public void SequentialQueueStopsAfterLastSong()
    {
        var queue = new PlaybackQueue();
        queue.Replace(new[] { Song("a"), Song("b") });

        Assert.Equal("b", queue.Next(PlaybackMode.Sequential)?.Title);
        Assert.Null(queue.Next(PlaybackMode.Sequential));
    }

    [Fact]
    public void RepeatOneKeepsCurrentSong()
    {
        var queue = new PlaybackQueue();
        queue.Replace(new[] { Song("a") });

        Assert.Equal("a", queue.Next(PlaybackMode.RepeatOne)?.Title);
    }

    private static Song Song(string title) => new() { Id = title, Path = title, Title = title };
}
