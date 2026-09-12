using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Xstar.Music.App;

/// <summary>
/// Extracts a vibrant, accessible accent colour from album artwork.
/// Mirrors the algorithm demonstrated in docs/ui-redesign-prototype.html.
/// </summary>
public static class CoverColorExtractor
{
    public static Color? Extract(BitmapSource? source)
    {
        if (source is null || source.PixelWidth <= 0 || source.PixelHeight <= 0) return null;
        try
        {
            var scaleX = 48.0 / source.PixelWidth;
            var scaleY = 48.0 / source.PixelHeight;
            var scaled = new TransformedBitmap(source, new ScaleTransform(scaleX, scaleY));
            var bitmap = new FormatConvertedBitmap(scaled, PixelFormats.Bgra32, null, 0);

            var width = bitmap.PixelWidth;
            var height = bitmap.PixelHeight;
            var stride = width * 4;
            var pixels = new byte[height * stride];
            bitmap.CopyPixels(pixels, stride, 0);

            var buckets = new (double Weight, double R, double G, double B)[24];
            for (var i = 0; i < pixels.Length; i += 4)
            {
                double b = pixels[i], g = pixels[i + 1], r = pixels[i + 2], a = pixels[i + 3];
                if (a < 40) continue;
                var (h, s, l) = RgbToHsl(r, g, b);
                if (s < 0.18 || l < 0.10 || l > 0.94) continue;
                var bucket = Math.Min(23, (int)(h * 24));
                var weight = s * (0.5 + Math.Abs(l - 0.5));
                buckets[bucket].Weight += weight;
                buckets[bucket].R += r * weight;
                buckets[bucket].G += g * weight;
                buckets[bucket].B += b * weight;
            }

            var best = buckets[0];
            foreach (var bucket in buckets)
                if (bucket.Weight > best.Weight) best = bucket;
            if (best.Weight <= 0) return null;

            var (hh, ss, ll) = RgbToHsl(best.R / best.Weight, best.G / best.Weight, best.B / best.Weight);
            ss = Math.Min(0.82, Math.Max(0.5, ss));
            ll = Math.Min(0.68, Math.Max(0.55, ll));
            var (rr, gg, bb) = HslToRgb(hh, ss, ll);
            return Color.FromRgb(rr, gg, bb);
        }
        catch
        {
            return null;
        }
    }

    private static (double H, double S, double L) RgbToHsl(double r, double g, double b)
    {
        r /= 255; g /= 255; b /= 255;
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        double h = 0, s = 0;
        var l = (max + min) / 2;
        if (Math.Abs(max - min) > double.Epsilon)
        {
            var d = max - min;
            s = l > 0.5 ? d / (2 - max - min) : d / (max + min);
            if (max == r) h = (g - b) / d + (g < b ? 6 : 0);
            else if (max == g) h = (b - r) / d + 2;
            else h = (r - g) / d + 4;
            h /= 6;
        }
        return (h, s, l);
    }

    private static (byte R, byte G, byte B) HslToRgb(double h, double s, double l)
    {
        double r, g, b;
        if (s == 0)
        {
            r = g = b = l;
        }
        else
        {
            double Hue(double p, double q, double t)
            {
                if (t < 0) t += 1;
                if (t > 1) t -= 1;
                if (t < 1.0 / 6) return p + (q - p) * 6 * t;
                if (t < 1.0 / 2) return q;
                if (t < 2.0 / 3) return p + (q - p) * (2.0 / 3 - t) * 6;
                return p;
            }

            var q = l < 0.5 ? l * (1 + s) : l + s - l * s;
            var p = 2 * l - q;
            r = Hue(p, q, h + 1.0 / 3);
            g = Hue(p, q, h);
            b = Hue(p, q, h - 1.0 / 3);
        }
        return ((byte)Math.Round(r * 255), (byte)Math.Round(g * 255), (byte)Math.Round(b * 255));
    }
}
