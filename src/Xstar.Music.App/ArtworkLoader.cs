using System.IO;
using System.Windows.Media.Imaging;
using Xstar.Music.Core;

namespace Xstar.Music.App;

/// <summary>Loads and caches artwork bitmaps from disk on the UI thread.</summary>
public static class ArtworkLoader
{
    private static readonly string[] SidecarNames = { "cover", "folder", "album", "front", "artist", "artwork" };
    private static readonly string[] SidecarExtensions = { ".jpg", ".jpeg", ".png", ".bmp", ".gif" };

    public static BitmapImage? Load(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
        try
        {
            using var stream = File.OpenRead(path);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Artwork for a song: the embedded/album image first, then a sidecar cover image
    /// (cover.jpg / folder.jpg / album.png / artist.jpg / &lt;filename&gt;.jpg) in the song folder.
    /// </summary>
    public static BitmapImage? LoadForSong(Song song)
    {
        var embedded = Load(song.ArtworkPath);
        if (embedded is not null) return embedded;

        try
        {
            var directory = Path.GetDirectoryName(song.Path);
            if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory)) return null;

            foreach (var name in SidecarNames)
            {
                foreach (var extension in SidecarExtensions)
                {
                    var candidate = Path.Combine(directory, name + extension);
                    if (File.Exists(candidate))
                    {
                        var image = Load(candidate);
                        if (image is not null) return image;
                    }
                }
            }

            var baseName = Path.GetFileNameWithoutExtension(song.Path);
            foreach (var extension in SidecarExtensions)
            {
                var candidate = Path.Combine(directory, baseName + extension);
                if (File.Exists(candidate))
                {
                    var image = Load(candidate);
                    if (image is not null) return image;
                }
            }

            foreach (var extension in SidecarExtensions)
            {
                var match = Directory.GetFiles(directory, "*" + extension);
                if (match.Length > 0)
                {
                    var image = Load(match[0]);
                    if (image is not null) return image;
                }
            }
        }
        catch
        {
            // ignore unreadable folders
        }

        return null;
    }
}
