namespace Xstar.Music.Core;

public sealed class LibraryScanner
{
    private readonly AudioMetadataReader _metadata = new();
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    { ".mp3", ".m4a", ".aac", ".flac", ".ogg", ".opus", ".wav", ".wma" };

    public async Task<IReadOnlyList<Song>> ScanAsync(IEnumerable<string> folders, IProgress<int>? progress = null, CancellationToken cancellationToken = default)
    {
        var files = folders.Where(Directory.Exists)
            .SelectMany(EnumerateFilesSafely)
            .Where(path => Extensions.Contains(Path.GetExtension(path)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var songs = new List<Song>(files.Length);
        for (var i = 0; i < files.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            songs.Add(_metadata.Read(files[i]));
            progress?.Report(i + 1);
            if (i % 64 == 0) await Task.Yield();
        }
        return songs;
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
