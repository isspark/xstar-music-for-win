using TagLib;

namespace Xstar.Music.Core;

public sealed class AudioMetadataReader
{
    private readonly string _artworkDirectory;

    public AudioMetadataReader(string? artworkDirectory = null)
    {
        _artworkDirectory = artworkDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "XstarMusic", "artwork");
        Directory.CreateDirectory(_artworkDirectory);
    }

    public Song Read(string path)
    {
        var fileInfo = new FileInfo(path);
        var id = StableId(path);
        var fallbackTitle = Path.GetFileNameWithoutExtension(path);
        try
        {
            using var file = TagLib.File.Create(path);
            var tag = file.Tag;
            var format = Path.GetExtension(path).TrimStart('.').ToUpperInvariant();
            var sampleRate = (int)file.Properties.AudioSampleRate;
            var bitDepth = (int)file.Properties.BitsPerSample;
            var bitrate = (int)file.Properties.AudioBitrate;
            var quality = QualityClassifier.Classify(format, bitrate, sampleRate, bitDepth);
            var artworkPath = SaveArtwork(id, file.Tag.Pictures.FirstOrDefault());
            return new Song
            {
                Id = id,
                Path = path,
                Title = First(tag.Title, fallbackTitle),
                Artist = First(tag.Performers.FirstOrDefault(), "未知艺术家"),
                Album = First(tag.Album, "未知专辑"),
                Format = format,
                Codec = file.Properties.Codecs.FirstOrDefault()?.ToString() ?? format,
                DurationMs = (long)file.Properties.Duration.TotalMilliseconds,
                Quality = quality,
                ArtworkPath = artworkPath,
                Bitrate = bitrate,
                SampleRate = sampleRate,
                BitDepth = bitDepth,
                Channels = (int)file.Properties.AudioChannels,
                FileSize = fileInfo.Exists ? fileInfo.Length : 0,
                LastWriteTimeUtcTicks = fileInfo.Exists ? fileInfo.LastWriteTimeUtc.Ticks : 0
            };
        }
        catch
        {
            return new Song { Id = id, Path = path, Title = fallbackTitle, Format = Path.GetExtension(path).TrimStart('.').ToUpperInvariant(), Quality = "普通", FileSize = fileInfo.Exists ? fileInfo.Length : 0, LastWriteTimeUtcTicks = fileInfo.Exists ? fileInfo.LastWriteTimeUtc.Ticks : 0 };
        }
    }

    private string? SaveArtwork(string id, IPicture? picture)
    {
        if (picture?.Data is null || picture.Data.Count == 0) return null;
        var path = Path.Combine(_artworkDirectory, id + ".jpg");
        if (!System.IO.File.Exists(path)) System.IO.File.WriteAllBytes(path, picture.Data.Data);
        return path;
    }

    public static string StableId(string path) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(path)))).ToLowerInvariant()[..16];

    private static string First(string? value, string fallback) => string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

}
