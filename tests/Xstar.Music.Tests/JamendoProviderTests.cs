using System.Net;
using System.Text;
using Xstar.Music.Core;

namespace Xstar.Music.Tests;

public sealed class JamendoProviderTests : IDisposable
{
    private readonly string _downloadDirectory;

    public JamendoProviderTests()
    {
        _downloadDirectory = Path.Combine(Path.GetTempPath(), "xstar-jamendo-" + Guid.NewGuid().ToString("N"));
    }

    [Fact]
    public async Task SearchParsesTracks()
    {
        const string json = """
            { "results": [
              { "id": "1", "name": "Dream", "artist_name": "Aster", "album_name": "Skies", "duration": 210, "audio": "https://x/stream", "audiodownload": "https://x/dl", "audiodownload_allowed": true, "image": "https://x/img" },
              { "id": "2", "name": "Night", "artist_name": "Aster", "album_name": "Skies", "duration": 180, "audio": "https://x/stream2", "audiodownload": "https://x/dl2", "audiodownload_allowed": false }
            ] }
            """;
        var provider = new JamendoProvider(new StubHandler(json)) { ClientId = "test-client" };

        var tracks = await provider.SearchAsync("aster");

        Assert.Equal(2, tracks.Count);
        Assert.Equal("jamendo:1", tracks[0].Id);
        Assert.Equal("Dream", tracks[0].Title);
        Assert.Equal(210000, tracks[0].DurationMs);
        Assert.True(tracks[0].Downloadable);
        Assert.False(tracks[1].Downloadable);
    }

    [Fact]
    public async Task SearchWithoutClientIdReturnsEmpty()
    {
        var provider = new JamendoProvider(new StubHandler("{\"results\":[]}"));
        Assert.Empty(await provider.SearchAsync("anything"));
    }

    [Fact]
    public async Task DownloadWritesFile()
    {
        var provider = new JamendoProvider(new StubHandler("", isBinary: true));
        var track = new OnlineTrack("jamendo:9", "Dream", "Aster", "Skies", 1000, "https://x/stream", "https://x/dl", null);

        var path = await provider.DownloadAsync(track, _downloadDirectory);

        Assert.True(File.Exists(path));
        Assert.Equal("Aster - Dream.mp3", Path.GetFileName(path));
        Assert.True(new FileInfo(path).Length > 0);
    }

    public void Dispose()
    {
        try { Directory.Delete(_downloadDirectory, true); } catch { }
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly string _json;
        private readonly bool _binary;
        public StubHandler(string json, bool isBinary = false) { _json = json; _binary = isBinary; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var content = _binary ? new ByteArrayContent(Encoding.UTF8.GetBytes("ID3FAKEAUDIO")) : new StringContent(_json, Encoding.UTF8, "application/json");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }
}
