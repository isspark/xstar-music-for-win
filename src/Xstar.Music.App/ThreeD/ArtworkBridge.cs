using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Xstar.Music.App.ThreeD;

/// <summary>
/// Encodes artwork into a small JPEG data URI so the 3D web view can use it as a texture.
/// Data URIs avoid the cross-origin restriction a separate virtual host would hit (the page runs
/// on xstar.local). Encode() additionally caches per song key so the main cover is only encoded once.
/// </summary>
public static class ArtworkBridge
{
    private const int CoverSize = 384;

    private static string? _key;
    private static string? _dataUri;

    /// <summary>Cached encode for the current now-playing cover.</summary>
    public static string? Encode(BitmapSource? artwork, string? key)
    {
        if (artwork is null) return null;
        if (!string.IsNullOrEmpty(key) && key == _key) return _dataUri;
        var data = EncodeBitmap(artwork, CoverSize);
        _key = key;
        _dataUri = data;
        return data;
    }

    /// <summary>Encodes an arbitrary bitmap (no caching). Returns null on failure.</summary>
    public static string? EncodeBitmap(BitmapSource? artwork, int maxSize)
    {
        if (artwork is null) return null;
        try
        {
            BitmapSource source = artwork;
            if (maxSize > 0 && (artwork.PixelWidth > maxSize || artwork.PixelHeight > maxSize))
            {
                double scale = Math.Min((double)maxSize / artwork.PixelWidth, (double)maxSize / artwork.PixelHeight);
                var transformed = new TransformedBitmap(artwork, new ScaleTransform(scale, scale));
                transformed.Freeze();
                source = transformed;
            }

            var encoder = new JpegBitmapEncoder { QualityLevel = 85 };
            encoder.Frames.Add(BitmapFrame.Create(source));
            using var stream = new MemoryStream();
            encoder.Save(stream);
            return "data:image/jpeg;base64," + Convert.ToBase64String(stream.ToArray());
        }
        catch
        {
            return null;
        }
    }

    public static void Reset()
    {
        _key = null;
        _dataUri = null;
    }
}
