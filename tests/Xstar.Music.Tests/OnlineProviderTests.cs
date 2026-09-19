using System.Net;
using System.Text;
using Xstar.Music.Core;

namespace Xstar.Music.Tests;

public sealed class GdStudioProviderTests
{
    [Fact]
    public async Task SearchParsesTracks()
    {
        const string json = """
            [
              { "id": "186016", "name": "晴天", "artist": ["周杰伦"], "album": "叶惠美", "pic_id": "109951", "url_id": "186016", "lyric_id": "186016", "source": "netease" }
            ]
            """;
        var provider = new GdStudioProvider(new StubHandler(json));

        var tracks = await provider.SearchAsync("晴天");

        Assert.Single(tracks);
        Assert.Equal("netease|186016", tracks[0].Id);
        Assert.Equal("gdstudio", tracks[0].ProviderKey);
        Assert.Equal("晴天", tracks[0].Title);
        Assert.Equal("周杰伦", tracks[0].Artist);
        Assert.Equal("叶惠美", tracks[0].Album);
    }

    [Fact]
    public async Task SearchAcceptsWrappedData()
    {
        const string json = """{ "data": [ { "id": "1", "name": "A", "artist": ["B"], "source": "netease" } ] }""";
        var provider = new GdStudioProvider(new StubHandler(json));

        var tracks = await provider.SearchAsync("a");

        Assert.Single(tracks);
        Assert.Equal("netease|1", tracks[0].Id);
    }

    [Fact]
    public async Task ResolveStreamUrlReadsUrl()
    {
        const string json = """{ "url": "http://cdn.example/song.mp3", "br": 320, "size": 1024 }""";
        var provider = new GdStudioProvider(new StubHandler(json)) { Bitrate = 320 };
        var track = new OnlineTrack("netease|186016", "晴天", "周杰伦", "叶惠美", 0, null, null, null) { ProviderKey = "gdstudio" };

        var url = await provider.ResolveStreamUrlAsync(track);

        Assert.Equal("http://cdn.example/song.mp3", url);
    }

    [Fact]
    public async Task UnreachableServiceThrows()
    {
        var provider = new GdStudioProvider(new FailingHandler());

        await Assert.ThrowsAsync<HttpRequestException>(() => provider.SearchAsync("x"));
    }

    private sealed class FailingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly string _json;
        public StubHandler(string json) => _json = json;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_json, Encoding.UTF8, "application/json")
            });
    }
}

public sealed class AudiusProviderTests
{
    [Fact]
    public async Task SearchParsesTracks()
    {
        const string json = """
            { "data": [
              { "id": "D7KyD", "title": "Waves", "duration": 200, "is_downloadable": true,
                "user": { "name": "Aster" }, "artwork": { "150x150": "https://img/150", "480x480": "https://img/480" } }
            ] }
            """;
        var provider = new AudiusProvider(new StubHandler(json));

        var tracks = await provider.SearchAsync("waves");

        Assert.Single(tracks);
        Assert.Equal("D7KyD", tracks[0].Id);
        Assert.Equal("audius", tracks[0].ProviderKey);
        Assert.Equal("Waves", tracks[0].Title);
        Assert.Equal("Aster", tracks[0].Artist);
        Assert.Equal(200000, tracks[0].DurationMs);
        Assert.Equal("https://img/480", tracks[0].ImageUrl);
        Assert.True(tracks[0].Downloadable);
        Assert.Contains("/tracks/D7KyD/stream", tracks[0].StreamUrl);
    }

    [Fact]
    public async Task NonDownloadableTrackHasNoDownloadUrl()
    {
        const string json = """{ "data": [ { "id": "x", "title": "T", "duration": 1, "is_downloadable": false, "user": { "name": "U" } } ] }""";
        var provider = new AudiusProvider(new StubHandler(json));

        var tracks = await provider.SearchAsync("t");

        Assert.False(tracks[0].Downloadable);
        Assert.NotNull(tracks[0].StreamUrl);
    }

    [Fact]
    public async Task NonStreamableTracksAreSkipped()
    {
        const string json = """
            { "data": [
              { "id": "x", "title": "Gated", "duration": 1, "is_streamable": false, "user": { "name": "U" } },
              { "id": "y", "title": "Ok", "duration": 2, "is_streamable": true, "user": { "name": "U" } }
            ] }
            """;
        var provider = new AudiusProvider(new StubHandler(json));

        var tracks = await provider.SearchAsync("t");

        Assert.Single(tracks);
        Assert.Equal("y", tracks[0].Id);
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly string _json;
        public StubHandler(string json) => _json = json;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_json, Encoding.UTF8, "application/json")
            });
    }
}

public sealed class OnlineMusicServiceTests
{
    [Fact]
    public async Task SearchAllMergesConfiguredProviders()
    {
        var service = new OnlineMusicService(new IOnlineMusicProvider[]
        {
            new FakeProvider("a", true, "A"),
            new FakeProvider("b", true, "B"),
            new FakeProvider("c", false, "C")
        });

        var tracks = await service.SearchAsync("q", OnlineMusicService.AllProviders);

        Assert.Equal(2, tracks.Count);
        Assert.Contains(tracks, t => t.ProviderKey == "a");
        Assert.Contains(tracks, t => t.ProviderKey == "b");
    }

    [Fact]
    public async Task DownloadRoutesToOwningProvider()
    {
        var service = new OnlineMusicService(new IOnlineMusicProvider[] { new FakeProvider("a", true, "A") });
        var track = new OnlineTrack("1", "T", "Ar", "Al", 0, null, null, null) { ProviderKey = "a" };

        var result = await service.DownloadAsync(track, "irrelevant");

        Assert.Equal("a:1", result);
    }

    private sealed class FakeProvider : IOnlineMusicProvider
    {
        public FakeProvider(string key, bool configured, string title) { Key = key; IsConfigured = configured; _title = title; }
        private readonly string _title;
        public string Key { get; }
        public string DisplayName => Key;
        public bool IsConfigured { get; }
        public Task<IReadOnlyList<OnlineTrack>> SearchAsync(string query, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<OnlineTrack>>(new[] { new OnlineTrack("1", _title, "Ar", "Al", 0, null, null, null) { ProviderKey = Key } });
        public Task<string?> ResolveStreamUrlAsync(OnlineTrack track, CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
        public Task<string> DownloadAsync(OnlineTrack track, string directory, CancellationToken cancellationToken = default) => Task.FromResult($"{Key}:{track.Id}");
    }
}
