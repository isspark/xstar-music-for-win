using System.Net;
using System.Text;
using Xstar.Music.Core;

namespace Xstar.Music.Tests;

public sealed class OnlineLyricsProviderTests : IDisposable
{
    private readonly string _cacheDirectory;

    public OnlineLyricsProviderTests()
    {
        _cacheDirectory = Path.Combine(Path.GetTempPath(), "xstar-lyrics-" + Guid.NewGuid().ToString("N"));
    }

    [Fact]
    public async Task FetchesSyncedLyricsAndCaches()
    {
        const string json = """
            [
              { "trackName": "Song", "artistName": "Artist", "duration": 120, "syncedLyrics": "[00:01.00]line one\n[00:05.50]line two", "plainLyrics": null }
            ]
            """;
        var provider = new OnlineLyricsProvider(_cacheDirectory, new StubHandler(json));
        var song = new Song { Id = "abc", Title = "Song", Artist = "Artist", DurationMs = 120000 };

        var lines = await provider.FetchAsync(song);

        Assert.Equal(2, lines.Count);
        Assert.Equal("line one", lines[0].Text);
        Assert.Equal(1000, lines[0].TimeMs);
        Assert.True(provider.HasCache("abc"));
        Assert.Equal(2, (await provider.FetchAsync(song)).Count);
    }

    [Fact]
    public async Task FallsBackToPlainLyricsWhenSyncedMissing()
    {
        const string json = """
            [
              { "trackName": "Song", "artistName": "Artist", "duration": 0, "syncedLyrics": null, "plainLyrics": "alpha\nbeta" }
            ]
            """;
        var provider = new OnlineLyricsProvider(_cacheDirectory, new StubHandler(json));
        var song = new Song { Id = "plain", Title = "Song", Artist = "Artist", DurationMs = 0 };

        var lines = await provider.FetchAsync(song);

        Assert.Equal(2, lines.Count);
        Assert.Equal("alpha", lines[0].Text);
    }

    [Fact]
    public async Task ReturnsEmptyOnHttpError()
    {
        var provider = new OnlineLyricsProvider(_cacheDirectory, new StubHandler("{}", HttpStatusCode.NotFound));
        var song = new Song { Id = "missing", Title = "Song", Artist = "Artist" };
        Assert.Empty(await provider.FetchAsync(song));
    }

    public void Dispose()
    {
        try { Directory.Delete(_cacheDirectory, true); } catch { }
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly string _json;
        private readonly HttpStatusCode _status;
        public StubHandler(string json, HttpStatusCode status = HttpStatusCode.OK) { _json = json; _status = status; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(_status) { Content = new StringContent(_json, Encoding.UTF8, "application/json") });
    }
}
