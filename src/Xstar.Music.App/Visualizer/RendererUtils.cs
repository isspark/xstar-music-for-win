using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Xstar.Music.App.Visualizer;

/// <summary>
/// Shared helpers used across all visualizer renderers.
/// All methods assume the bitmap is already locked via <see cref="WriteableBitmap.Lock"/>.
/// Renderers MUST call Lock() at the start of Render() and Unlock()+Invalidate() at the end.
/// </summary>
internal static class RendererUtils
{
    public const int RenderWidth = 800;
    public const int RenderHeight = 600;

    /// <summary>Fallback backdrop used before the first themed frame arrives (deep indigo black).</summary>
    public static readonly Color DefaultBackground = Color.FromRgb(5, 4, 10);

    /// <summary>
    /// Builds the visualizer backdrop from the current theme accent: the canvas always stays
    /// very dark so additive glow effects read correctly, but it carries the accent hue so
    /// switching themes (e.g. deep-space blue → aurora teal → blossom pink) visibly tints it.
    /// </summary>
    public static Color ThemedBackground(Color accent)
    {
        var (h, s, _) = RgbToHsl(accent);
        return HslToRgb(h, Math.Clamp(s * 0.85, 0.35, 0.85), 0.045);
    }

    /// <summary>
    /// Adds a dirty rect then unlocks. Without AddDirtyRect, pixels written to the
    /// back buffer are never pushed to the compositor → a black/frozen image.
    /// </summary>
    public static void FlushLocked(WriteableBitmap bmp)
    {
        bmp.AddDirtyRect(new System.Windows.Int32Rect(0, 0, bmp.PixelWidth, bmp.PixelHeight));
        bmp.Unlock();
    }

    /// <summary>Additively brighten a pixel (clamped), scaled by [0..1]. Bitmap must be locked.</summary>
    public static unsafe void AddScaledLocked(WriteableBitmap bmp, int x, int y, byte r, byte g, byte b, float scale)
    {
        if (x < 0 || y < 0 || x >= bmp.PixelWidth || y >= bmp.PixelHeight) return;
        byte* px = (byte*)bmp.BackBuffer + y * bmp.BackBufferStride + x * 4;
        px[0] = (byte)Math.Min(255, (int)(px[0] + b * scale));
        px[1] = (byte)Math.Min(255, (int)(px[1] + g * scale));
        px[2] = (byte)Math.Min(255, (int)(px[2] + r * scale));
    }

    /// <summary>
    /// Draw a radial "bloom" glow at (cx,cy) that additively brightens its surroundings.
    /// Intensity falls off like a Gaussian. Bitmap must be locked.
    /// step&gt;1 downsamples (writes step×step blocks) for large soft auras.
    /// </summary>
    public static unsafe void GlowLocked(WriteableBitmap bmp, int cx, int cy, float radius, byte r, byte g, byte b, float strength, int step = 1)
    {
        if (radius < 0.5f) { AddScaledLocked(bmp, cx, cy, r, g, b, strength); return; }
        float rr = Math.Max(1, radius);
        float sigma = rr * 0.42f;
        float inv2s2 = 1f / (2f * sigma * sigma);
        int lim = (int)Math.Ceiling(rr);
        int w = bmp.PixelWidth, h = bmp.PixelHeight, stride = bmp.BackBufferStride;
        if (step < 1) step = 1;
        byte* basePtr = (byte*)bmp.BackBuffer;
        for (int dy = -lim; dy <= lim; dy += step)
        {
            int y = cy + dy;
            if (y < 0 || y >= h) continue;
            int bh = Math.Min(step, h - y);
            float fy = dy * dy;
            byte* row = basePtr + y * stride;
            for (int dx = -lim; dx <= lim; dx += step)
            {
                int x = cx + dx;
                if (x < 0 || x >= w) continue;
                float d2v = (fy + dx * dx) * inv2s2;
                if (d2v > 4.5f) continue;
                float v = strength * MathF.Exp(-d2v);
                if (v < 0.012f) continue;
                int bw = Math.Min(step, w - x);
                for (int yy = 0; yy < bh; yy++)
                {
                    byte* px = row + yy * stride + x * 4;
                    for (int xx = 0; xx < bw; xx++, px += 4)
                    {
                        px[0] = (byte)Math.Min(255, (int)(px[0] + b * v));
                        px[1] = (byte)Math.Min(255, (int)(px[1] + g * v));
                        px[2] = (byte)Math.Min(255, (int)(px[2] + r * v));
                    }
                }
            }
        }
    }

    /// <summary>Draw a filled vertical-gradient bar (top bright → bottom dim) at given rect.</summary>
    public static unsafe void FillVerticalGradientLocked(WriteableBitmap bmp, int x0, int y0, int w, int h,
        System.Windows.Media.Color top, System.Windows.Media.Color bottom)
    {
        if (w <= 0 || h <= 0) return;
        int maxW = bmp.PixelWidth, maxH = bmp.PixelHeight;
        int xc = Math.Clamp(x0, 0, maxW - 1);
        int yc = Math.Clamp(y0, 0, maxH - 1);
        int wc = Math.Min(w, maxW - xc);
        int hc = Math.Min(h, maxH - yc);
        int stride = bmp.BackBufferStride;
        byte* basePtr = (byte*)bmp.BackBuffer;
        float inv = 1f / Math.Max(1, h - 1);
        for (int yy = 0; yy < hc; yy++)
        {
            float t = yy * inv;
            byte rr = (byte)(top.R * (1 - t) + bottom.R * t);
            byte gg = (byte)(top.G * (1 - t) + bottom.G * t);
            byte bb = (byte)(top.B * (1 - t) + bottom.B * t);
            byte* row = basePtr + (yc + yy) * stride + xc * 4;
            for (int xx = 0; xx < wc * 4; xx += 4)
            {
                row[xx] = bb; row[xx + 1] = gg; row[xx + 2] = rr; row[xx + 3] = 255;
            }
        }
    }

    /// <summary>Fill the entire bitmap with a solid BGRA colour. Bitmap must be locked.</summary>
    public static unsafe void ClearLocked(WriteableBitmap bmp, byte r, byte g, byte b, byte a = 255)
    {
        int stride = bmp.BackBufferStride;
        int height = bmp.PixelHeight;
        byte* ptr = (byte*)bmp.BackBuffer;
        for (int y = 0; y < height; y++)
        {
            byte* row = ptr + y * stride;
            for (int x = 0; x < stride; x += 4)
            {
                row[x] = b; row[x + 1] = g; row[x + 2] = r; row[x + 3] = a;
            }
        }
    }

    /// <summary>Fill the entire bitmap with a solid colour. Bitmap must be locked.</summary>
    public static void ClearLocked(WriteableBitmap bmp, Color color)
        => ClearLocked(bmp, color.R, color.G, color.B, color.A);

    /// <summary>
    /// Per-pixel trail decay: each pixel fades toward <paramref name="target"/> (the themed
    /// backdrop) instead of black, so glow trails dissolve into the active background colour.
    /// factor 0..1 — how much of the existing pixel is retained per frame. Bitmap must be locked.
    /// </summary>
    public static unsafe void DecayToLocked(WriteableBitmap bmp, byte r, byte g, byte b, float factor)
    {
        factor = Math.Clamp(factor, 0f, 1f);
        float keep = factor, drop = 1f - factor;
        int stride = bmp.BackBufferStride;
        int height = bmp.PixelHeight;
        int pxStride = bmp.PixelWidth * 4;
        byte* ptr = (byte*)bmp.BackBuffer;
        for (int y = 0; y < height; y++)
        {
            byte* row = ptr + y * stride;
            for (int x = 0; x < pxStride; x += 4)
            {
                row[x] = (byte)(row[x] * keep + b * drop);
                row[x + 1] = (byte)(row[x + 1] * keep + g * drop);
                row[x + 2] = (byte)(row[x + 2] * keep + r * drop);
            }
        }
    }

    /// <inheritdoc cref="DecayToLocked(WriteableBitmap, byte, byte, byte, float)"/>
    public static void DecayToLocked(WriteableBitmap bmp, Color target, float factor)
        => DecayToLocked(bmp, target.R, target.G, target.B, factor);

    /// <summary>Put a single BGRA pixel. Bitmap must be locked.</summary>
    public static unsafe void PutPixelLocked(WriteableBitmap bmp, int x, int y, byte r, byte g, byte b, byte a = 255)
    {
        int stride = bmp.BackBufferStride;
        byte* ptr = (byte*)bmp.BackBuffer;
        byte* px = ptr + y * stride + x * 4;
        px[0] = b; px[1] = g; px[2] = r; px[3] = a;
    }

    /// <summary>
    /// Draw a filled rectangle. Coordinates are clamped. Bitmap must be locked.
    /// </summary>
    public static unsafe void FillRectLocked(WriteableBitmap bmp, int x0, int y0, int w, int h, byte r, byte g, byte b, byte a = 255)
    {
        if (w <= 0 || h <= 0) return;
        int maxW = bmp.PixelWidth, maxH = bmp.PixelHeight;
        x0 = Math.Clamp(x0, 0, maxW - 1);
        y0 = Math.Clamp(y0, 0, maxH - 1);
        w = Math.Min(w, maxW - x0);
        h = Math.Min(h, maxH - y0);
        int stride = bmp.BackBufferStride;
        byte* ptr = (byte*)bmp.BackBuffer;
        for (int y = y0; y < y0 + h; y++)
        {
            byte* row = ptr + y * stride + x0 * 4;
            for (int x = 0; x < w * 4; x += 4)
            {
                row[x] = b; row[x + 1] = g; row[x + 2] = r; row[x + 3] = a;
            }
        }
    }

    /// <summary>
    /// A vertex for <see cref="FillConvexPolyLocked"/>: position + flat RGB colour
    /// (Gouraud-interpolated across scanlines).
    /// </summary>
    public readonly struct PolyVertex
    {
        public readonly int X, Y;
        public readonly byte R, G, B;
        public PolyVertex(int x, int y, byte r, byte g, byte b) { X = x; Y = y; R = r; G = g; B = b; }
        public PolyVertex(int x, int y, Color c) { X = x; Y = y; R = c.R; G = c.G; B = c.B; }
    }

    /// <summary>
    /// Fills a convex polygon (triangle fan / quad / ring wedge) with per-vertex colours
    /// interpolated along each scanline. Overwrites pixels (opaque). Bitmap must be locked.
    /// </summary>
    public static unsafe void FillConvexPolyLocked(WriteableBitmap bmp, ReadOnlySpan<PolyVertex> verts)
    {
        int n = verts.Length;
        if (n < 3) return;

        int minY = int.MaxValue, maxY = int.MinValue;
        for (int i = 0; i < n; i++)
        {
            if (verts[i].Y < minY) minY = verts[i].Y;
            if (verts[i].Y > maxY) maxY = verts[i].Y;
        }
        minY = Math.Max(minY, 0);
        maxY = Math.Min(maxY, bmp.PixelHeight - 1);
        if (minY > maxY) return;

        int bmpW = bmp.PixelWidth, stride = bmp.BackBufferStride;
        byte* basePtr = (byte*)bmp.BackBuffer;

        // A convex polygon has at most n intersections per scanline.
        float* ix = stackalloc float[n];
        float* ir = stackalloc float[n];
        float* ig = stackalloc float[n];
        float* ib = stackalloc float[n];

        for (int y = minY; y <= maxY; y++)
        {
            int cnt = 0;
            for (int e = 0; e < n; e++)
            {
                var a = verts[e];
                var c = verts[(e + 1) % n];
                if (a.Y == c.Y) continue;
                // Half-open edge convention so shared edges are sampled once.
                if (y < Math.Min(a.Y, c.Y) || y >= Math.Max(a.Y, c.Y)) continue;
                float t = (y + 0.5f - a.Y) / (c.Y - a.Y);
                ix[cnt] = a.X + t * (c.X - a.X);
                ir[cnt] = a.R + t * (c.R - a.R);
                ig[cnt] = a.G + t * (c.G - a.G);
                ib[cnt] = a.B + t * (c.B - a.B);
                cnt++;
            }
            if (cnt < 2) continue;

            // Insertion sort by x (n is tiny).
            for (int a = 1; a < cnt; a++)
            {
                float kx = ix[a], kr = ir[a], kg = ig[a], kb = ib[a];
                int z = a - 1;
                while (z >= 0 && ix[z] > kx)
                {
                    ix[z + 1] = ix[z]; ir[z + 1] = ir[z]; ig[z + 1] = ig[z]; ib[z + 1] = ib[z];
                    z--;
                }
                ix[z + 1] = kx; ir[z + 1] = kr; ig[z + 1] = kg; ib[z + 1] = kb;
            }

            for (int p = 0; p + 1 < cnt; p += 2)
            {
                float xL = ix[p], xR = ix[p + 1];
                int xs = Math.Max(0, (int)MathF.Ceiling(xL - 0.5f));
                int xe = Math.Min(bmpW - 1, (int)MathF.Floor(xR - 0.5f));
                if (xe < xs) continue;
                float span = MathF.Max(1f, xR - xL);
                byte* row = basePtr + y * stride + xs * 4;
                for (int x = xs; x <= xe; x++, row += 4)
                {
                    float t = Math.Clamp((x + 0.5f - xL) / span, 0f, 1f);
                    row[0] = (byte)(ib[p] + (ib[p + 1] - ib[p]) * t);
                    row[1] = (byte)(ig[p] + (ig[p + 1] - ig[p]) * t);
                    row[2] = (byte)(ir[p] + (ir[p + 1] - ir[p]) * t);
                    row[3] = 255;
                }
            }
        }
    }

    /// <summary>
    /// Draws a 1px line with per-endpoint colours (opaque overwrite). Bitmap must be locked.
    /// </summary>
    public static unsafe void DrawLineLocked(WriteableBitmap bmp, int x0, int y0, Color c0, int x1, int y1, Color c1)
    {
        int steps = Math.Max(Math.Abs(x1 - x0), Math.Abs(y1 - y0));
        if (steps == 0) { FillRectLocked(bmp, x0, y0, 1, 1, c0.R, c0.G, c0.B); return; }
        for (int i = 0; i <= steps; i++)
        {
            float t = i / (float)steps;
            int x = (int)Math.Round(x0 + (x1 - x0) * t);
            int y = (int)Math.Round(y0 + (y1 - y0) * t);
            byte r = (byte)(c0.R + (c1.R - c0.R) * t);
            byte g = (byte)(c0.G + (c1.G - c0.G) * t);
            byte bl = (byte)(c0.B + (c1.B - c0.B) * t);
            FillRectLocked(bmp, x, y, 1, 1, r, g, bl);
        }
    }

    /// <summary>
    /// Draws a thin additive line with per-endpoint colours and a 1px soft edge (~3px wide),
    /// so coloured strands read crisply without a large bloom. Bitmap must be locked.
    /// </summary>
    public static unsafe void DrawSoftLineAddLocked(WriteableBitmap bmp,
        float x0, float y0, System.Windows.Media.Color c0,
        float x1, float y1, System.Windows.Media.Color c1, float intensity)
    {
        if (intensity <= 0.004f) return;
        int steps = (int)MathF.Ceiling(MathF.Max(MathF.Abs(x1 - x0), MathF.Abs(y1 - y0)));
        if (steps <= 0)
        {
            int px = (int)MathF.Round(x0), py = (int)MathF.Round(y0);
            AddScaledLocked(bmp, px, py, c0.R, c0.G, c0.B, intensity);
            return;
        }
        float edge = intensity * 0.4f;
        for (int i = 0; i <= steps; i++)
        {
            float t = i / (float)steps;
            int x = (int)MathF.Round(x0 + (x1 - x0) * t);
            int y = (int)MathF.Round(y0 + (y1 - y0) * t);
            byte r = (byte)(c0.R + (c1.R - c0.R) * t);
            byte g = (byte)(c0.G + (c1.G - c0.G) * t);
            byte b = (byte)(c0.B + (c1.B - c0.B) * t);
            AddScaledLocked(bmp, x, y, r, g, b, intensity);
            AddScaledLocked(bmp, x + 1, y, r, g, b, edge);
            AddScaledLocked(bmp, x - 1, y, r, g, b, edge);
            AddScaledLocked(bmp, x, y + 1, r, g, b, edge);
            AddScaledLocked(bmp, x, y - 1, r, g, b, edge);
        }
    }

    /// <summary>
    /// Draws a soft additive glow along a line (per-point radial falloff of the given radius),
    /// with per-endpoint colours. Use a small radius for a neon halo. Bitmap must be locked.
    /// </summary>
    public static unsafe void DrawGlowLineAddLocked(WriteableBitmap bmp,
        float x0, float y0, System.Windows.Media.Color c0,
        float x1, float y1, System.Windows.Media.Color c1, float intensity, int radius)
    {
        if (intensity <= 0.004f || radius < 1) return;
        int steps = (int)MathF.Ceiling(MathF.Max(MathF.Abs(x1 - x0), MathF.Abs(y1 - y0)));
        if (steps < 1) steps = 1;
        int r2 = radius * radius;
        float sigma = radius * 0.62f;
        float inv2s2 = 1f / (2f * sigma * sigma);
        for (int i = 0; i <= steps; i++)
        {
            float t = i / (float)steps;
            int x = (int)MathF.Round(x0 + (x1 - x0) * t);
            int y = (int)MathF.Round(y0 + (y1 - y0) * t);
            byte r = (byte)(c0.R + (c1.R - c0.R) * t);
            byte g = (byte)(c0.G + (c1.G - c0.G) * t);
            byte b = (byte)(c0.B + (c1.B - c0.B) * t);
            for (int dy = -radius; dy <= radius; dy++)
            {
                int d2y = dy * dy;
                for (int dx = -radius; dx <= radius; dx++)
                {
                    int d2 = d2y + dx * dx;
                    if (d2 > r2) continue;
                    float v = intensity * MathF.Exp(-d2 * inv2s2);
                    if (v < 0.01f) continue;
                    AddScaledLocked(bmp, x + dx, y + dy, r, g, b, v);
                }
            }
        }
    }

    /// <summary>Read a pixel's current BGRA colour. Bitmap must be locked.</summary>
    public static unsafe System.Windows.Media.Color ReadPixelLocked(WriteableBitmap bmp, int x, int y)
    {
        int stride = bmp.BackBufferStride;
        byte* ptr = (byte*)bmp.BackBuffer;
        byte* px = ptr + y * stride + x * 4;
        return System.Windows.Media.Color.FromArgb(px[3], px[2], px[1], px[0]);
    }

    /// <summary>HSL → RGB conversion. H is 0~360, S/L 0~1.</summary>
    public static System.Windows.Media.Color HslToRgb(double h, double s, double l, byte a = 255)
    {
        double r, g, bl;
        h = h % 360;
        if (h < 0) h += 360;
        double c = (1 - Math.Abs(2 * l - 1)) * s;
        double hh = h / 60.0;
        double x = c * (1 - Math.Abs(hh % 2 - 1));
        r = g = bl = 0;
        switch ((int)hh)
        {
            case 0: r = c; g = x; bl = 0; break;
            case 1: r = x; g = c; bl = 0; break;
            case 2: r = 0; g = c; bl = x; break;
            case 3: r = 0; g = x; bl = c; break;
            case 4: r = x; g = 0; bl = c; break;
            default: r = c; g = 0; bl = x; break;
        }
        double m = l - c / 2;
        return System.Windows.Media.Color.FromArgb(a,
            (byte)Math.Round((r + m) * 255),
            (byte)Math.Round((g + m) * 255),
            (byte)Math.Round((bl + m) * 255));
    }

    public static (double H, double S, double L) RgbToHsl(System.Windows.Media.Color c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double mx = Math.Max(r, Math.Max(g, b));
        double mn = Math.Min(r, Math.Min(g, b));
        double h = 0, s = 0;
        double l = (mx + mn) / 2;
        if (Math.Abs(mx - mn) > double.Epsilon)
        {
            double d = mx - mn;
            s = l > 0.5 ? d / (2 - mx - mn) : d / (mx + mn);
            if (mx == r) h = (g - b) / d + (g < b ? 6 : 0);
            else if (mx == g) h = (b - r) / d + 2;
            else h = (r - g) / d + 4;
            h *= 60;
        }
        return (h, s, l);
    }
}
