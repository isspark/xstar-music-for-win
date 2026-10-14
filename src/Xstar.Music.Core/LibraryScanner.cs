namespace Xstar.Music.Core;

public sealed class LibraryScanner
{
    private readonly AudioMetadataReader _metadata = new();
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    { ".mp3", ".m4a", ".aac", ".flac", ".ogg", ".opus", ".wav", ".wma" };

    /// <summary>
    /// Progress tuple: (scanned current file, total file count).
    /// </summary>
    public async Task<IReadOnlyList<Song>> ScanAsync(
        IEnumerable<string> folders,
        IProgress<(int Done, int Total, string Status)>? progress = null,
        CancellationToken cancellationToken = default,
        IEnumerable<Song>? cachedSongs = null)
    {
        var cache = (cachedSongs ?? Array.Empty<Song>())
            .ToDictionary(song => song.Path, StringComparer.OrdinalIgnoreCase);
        // Phase 1: fast file enumeration (just disk I/O, no heavy decode).
        progress?.Report((0, 0, "正在扫描文件夹…"));
        var files = folders.Where(Directory.Exists)
            .SelectMany(EnumerateFilesSafely)
            .Where(path => Extensions.Contains(Path.GetExtension(path)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var total = files.Length;
        progress?.Report((0, total, $"发现 {total} 个文件，检查缓存中…"));

        // Reuse persisted metadata when the file is unchanged, including across restarts.
        var songs = new List<Song>(total);
        var metadataReads = 0;
        for (var i = 0; i < total; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Song song;
            if (cache.TryGetValue(files[i], out var cached) && CanReuse(cached, files[i]))
                song = cached;
            else
            {
                song = await Task.Run(() => _metadata.Read(files[i]), cancellationToken);
                metadataReads++;
            }
            songs.Add(song);
            if (i % 8 == 0 || i == total - 1)
                progress?.Report((i + 1, total, $"检查文件 {i + 1}/{total}，读取元数据 {metadataReads} 个"));
        }
        progress?.Report((total, total, "扫描完成"));
        return songs;
    }

    private static bool CanReuse(Song song, string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists && info.Length == song.FileSize
                && info.LastWriteTimeUtc.Ticks == song.LastWriteTimeUtcTicks
                && (song.ArtworkPath is null || File.Exists(song.ArtworkPath));
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private static IEnumerable<string> EnumerateFilesSafely(string root)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            string[] files;
            string[] children;
            try
            {
                files = Directory.GetFiles(directory);
                children = Directory.GetDirectories(directory);
            }
            catch (UnauthorizedAccessException) { continue; }
            catch (DirectoryNotFoundException) { continue; }
            catch (IOException) { continue; }

            foreach (var file in files) yield return file;
            foreach (var child in children) pending.Push(child);
        }
    }
}
