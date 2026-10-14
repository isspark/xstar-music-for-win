using Xstar.Music.Core;

namespace Xstar.Music.Tests;

public sealed class LibraryAndDatabaseTests
{
    [Fact]
    public async Task ScansWavFileAndPersistsToDatabase()
    {
        var root = Path.Combine(Path.GetTempPath(), "xstar-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var wavPath = Path.Combine(root, "sample.wav");
            WriteSilentWav(wavPath, seconds: 1, sampleRate: 44100);

            var scanner = new LibraryScanner();
            var songs = await scanner.ScanAsync(new[] { root });

            var song = Assert.Single(songs);
            Assert.Equal("WAV", song.Format);
            Assert.Equal("sample", song.Title);
            Assert.Equal("无损", song.Quality);
            Assert.True(song.DurationMs > 500, $"duration was {song.DurationMs}");

            var databasePath = Path.Combine(root, "library.db");
            using (var database = new MusicDatabase(databasePath))
            {
                database.UpsertSongs(songs);
                database.ReplaceScanFiles(songs.Select(x => x.Path));
                var loaded = database.ReadSongs();
                var stored = Assert.Single(loaded);
                Assert.Equal(wavPath, stored.Path);
                Assert.Equal("sample", stored.Title);
            }

            Assert.True(File.Exists(databasePath));
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    [Fact]
    public async Task ReopenedDatabaseReusesUnchangedMetadata()
    {
        var root = Path.Combine(Path.GetTempPath(), "xstar-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var wavPath = Path.Combine(root, "sample.wav");
            WriteSilentWav(wavPath, seconds: 1, sampleRate: 44100);
            var databasePath = Path.Combine(root, "library.db");
            var info = new FileInfo(wavPath);
            using (var database = new MusicDatabase(databasePath))
            {
                database.UpsertSongs(new[] { new Song
                {
                    Id = AudioMetadataReader.StableId(wavPath), Path = wavPath,
                    Title = "持久化的元数据", FileSize = info.Length,
                    LastWriteTimeUtcTicks = info.LastWriteTimeUtc.Ticks
                } });
            }

            using var reopened = new MusicDatabase(databasePath);
            var songs = await new LibraryScanner().ScanAsync(new[] { root }, cachedSongs: reopened.ReadSongs());
            Assert.Equal("持久化的元数据", Assert.Single(songs).Title);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ChangedFilesRefreshMetadata(bool changeSize)
    {
        var root = Path.Combine(Path.GetTempPath(), "xstar-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var wavPath = Path.Combine(root, "sample.wav");
            WriteSilentWav(wavPath, seconds: 1, sampleRate: 44100);
            var scanner = new LibraryScanner();
            var cached = Assert.Single(await scanner.ScanAsync(new[] { root }));
            WriteSilentWav(wavPath, seconds: 2, sampleRate: changeSize ? 44100 : 22050);
            // Isolate each invalidation key: changed size with the original timestamp,
            // or changed audio properties and timestamp while retaining the same size.
            File.SetLastWriteTimeUtc(wavPath, new DateTime(cached.LastWriteTimeUtcTicks, DateTimeKind.Utc)
                .AddMinutes(changeSize ? 0 : 1));

            var refreshed = Assert.Single(await scanner.ScanAsync(new[] { root }, cachedSongs: new[] { cached }));
            Assert.True(refreshed.DurationMs > 1500);
            Assert.Equal(changeSize ? 44100 : 22050, refreshed.SampleRate);
            Assert.Equal(new FileInfo(wavPath).Length, refreshed.FileSize);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    private static void WriteSilentWav(string path, int seconds, int sampleRate)
    {
        const short channels = 1;
        const short bitsPerSample = 16;
        var dataLength = seconds * sampleRate * channels * (bitsPerSample / 8);
        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream);
        writer.Write(new[] { 'R', 'I', 'F', 'F' });
        writer.Write(36 + dataLength);
        writer.Write(new[] { 'W', 'A', 'V', 'E' });
        writer.Write(new[] { 'f', 'm', 't', ' ' });
        writer.Write(16);
        writer.Write((short)1);
        writer.Write(channels);
        writer.Write(sampleRate);
        writer.Write(sampleRate * channels * (bitsPerSample / 8));
        writer.Write((short)(channels * (bitsPerSample / 8)));
        writer.Write(bitsPerSample);
        writer.Write(new[] { 'd', 'a', 't', 'a' });
        writer.Write(dataLength);
        writer.Write(new byte[dataLength]);
    }
}
