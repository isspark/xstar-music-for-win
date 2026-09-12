using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Xstar.Music.App.Visualizer;

public sealed class NebulaRenderer : IVisualizerRenderer
{
    public string StyleId => "nebula";
    public string DisplayName => "星云";
    public bool IsHardwareCompatible => true;

    private WriteableBitmap? _bmp;
    private readonly Random _rng = new(11);
    private double _huePhase;
    private float _beatExpansion;
    private Point[]? _stars;

    public void Initialize(WriteableBitmap bitmap)
    {
        _bmp = bitmap;
        _huePhase = 0;
        _beatExpansion = 0;

        bitmap.Lock();
        try { RendererUtils.ClearLocked(bitmap, RendererUtils.DefaultBackground); }
        finally { RendererUtils.FlushLocked(bitmap); }
    }

    public void Render(VisualizerFrame frame)
    {
        if (_bmp is null) return;

        _bmp.Lock();
        try
        {
            int w = _bmp.PixelWidth, h = _bmp.PixelHeight;
            int cx = w / 2, cy = h / 2;

            // Strong decay for short-lived clouds (dissolves into the themed backdrop)
            RendererUtils.DecayToLocked(_bmp, frame.Background, 0.88f);

            float bass = frame.BassEnergy;
            float mid = frame.MidEnergy;
            float treble = frame.TrebleEnergy;

            _huePhase += frame.DeltaTimeMs * 0.02f;

            if (frame.BeatDetected) _beatExpansion = 1f;
            else _beatExpansion = Math.Max(0, _beatExpansion - 0.06f);

            var (baseHue, baseSat, _) = RendererUtils.RgbToHsl(frame.Accent);
            double offsetHue = (baseHue + _huePhase * 0.5) % 360;

            DrawStarField(_bmp, w, h, treble);

            float intensity = 0.3f + bass * 1.8f + _beatExpansion * 2.5f;
            float radius = (float)(Math.Min(w, h) * 0.32 * (1f + _beatExpansion * 0.4f));

            DrawNebulaBlob(_bmp, cx, cy, radius, intensity, offsetHue, baseSat, 0.4f);
            DrawNebulaBlob(_bmp, cx - (int)(radius / 4), cy + (int)(radius / 6), radius * 0.7f, intensity * 0.7f,
                (offsetHue + 40) % 360, baseSat, 0.3f);
            DrawNebulaBlob(_bmp, cx + (int)(radius / 3), cy - (int)(radius / 5), radius * 0.55f, intensity * 0.5f,
                (offsetHue + 80) % 360, baseSat, 0.2f);

            // Beat flash ring — drawn as additive blooms around the edge for a soft shockwave
            if (_beatExpansion > 0.2f)
            {
                float beatRadius = radius * (0.3f + _beatExpansion * 0.6f);
                float ringStrength = _beatExpansion * 1.4f;
                var ringCol = RendererUtils.HslToRgb((offsetHue + 20) % 360, baseSat, 0.85);
                int steps = Math.Max(16, (int)(beatRadius * 0.5f));
                for (int i = 0; i < steps; i++)
                {
                    double ang = (double)i / steps * Math.PI * 2;
                    int bx = cx + (int)(Math.Cos(ang) * beatRadius);
                    int by = cy + (int)(Math.Sin(ang) * beatRadius);
                    RendererUtils.GlowLocked(_bmp, bx, by, Math.Max(4, beatRadius * 0.06f),
                        ringCol.R, ringCol.G, ringCol.B, ringStrength);
                }
                RendererUtils.GlowLocked(_bmp, cx, cy, radius * 0.3f, ringCol.R, ringCol.G, ringCol.B, ringStrength * 0.5f);
            }

            // Evaporating sparkles at edges
            int sparkleCount = (int)(mid * 15 + treble * 10);
            for (int i = 0; i < sparkleCount; i++)
            {
                int sx = cx + _rng.Next(-(int)radius, (int)radius);
                int sy = cy + _rng.Next(-(int)radius, (int)radius);
                double dist = Math.Sqrt((sx - cx) * (sx - cx) + (sy - cy) * (sy - cy));
                if (dist < radius * 0.8) continue;
                if (sx < 0 || sx >= w || sy < 0 || sy >= h) continue;
                var c = RendererUtils.HslToRgb(offsetHue, baseSat * 0.9, 0.9 + _rng.NextDouble() * 0.1);
                RendererUtils.PutPixelLocked(_bmp, sx, sy, c.R, c.G, c.B);
            }
        }
        finally { RendererUtils.FlushLocked(_bmp); }
    }

    public void Cleanup() { _bmp = null; }

    private static void DrawNebulaBlob(WriteableBitmap bmp, int cx, int cy, float radius,
        float intensity, double hue, double sat, float brightnessMult)
    {
        int r = (int)Math.Ceiling(radius);
        var center = RendererUtils.HslToRgb(hue, sat, 0.85 * brightnessMult);
        var edge = RendererUtils.HslToRgb((hue + 30) % 360, sat, 0.45 * brightnessMult);

        int w = bmp.PixelWidth, h = bmp.PixelHeight;
        float sigma = radius * 0.35f;
        double sigma2 = 2 * sigma * sigma;
        for (int dy = -r; dy <= r; dy++)
        {
            for (int dx = -r; dx <= r; dx++)
            {
                double dist = Math.Sqrt(dx * dx + dy * dy);
                if (dist > r) continue;
                double val = Math.Exp(-dist * dist / sigma2);
                if (val < 0.005) continue;
                double v = val * intensity;
                if (v < 0.005) continue;

                int ix = cx + dx, iy = cy + dy;
                if (ix < 0 || ix >= w || iy < 0 || iy >= h) continue;

                double t = 1.0 - dist / r;
                byte rr = (byte)(edge.R * (1 - t) + center.R * t);
                byte gg = (byte)(edge.G * (1 - t) + center.G * t);
                byte bb = (byte)(edge.B * (1 - t) + center.B * t);

                var existing = RendererUtils.ReadPixelLocked(bmp, ix, iy);
                rr = (byte)Math.Clamp((existing.R * 0.5 + rr * v), 0, 255);
                gg = (byte)Math.Clamp((existing.G * 0.5 + gg * v), 0, 255);
                bb = (byte)Math.Clamp((existing.B * 0.5 + bb * v), 0, 255);
                RendererUtils.PutPixelLocked(bmp, ix, iy, rr, gg, bb);
            }
        }
    }

    private void DrawStarField(WriteableBitmap bmp, int w, int h, float treble)
    {
        if (_stars is null || _stars.Length == 0 || _stars[0].X < 0 || _stars[0].Y < 0 || bmp.PixelWidth != w)
        {
            _stars = Enumerable.Range(0, 90)
                .Select(_ => new System.Windows.Point(_rng.Next(w), _rng.Next(h)))
                .ToArray();
        }

        foreach (var p in _stars)
        {
            float flicker = (float)(0.3 + _rng.NextDouble() * 0.6) * (0.5f + treble * 1.4f);
            if (flicker < 0.08f) continue;
            byte intensity = (byte)Math.Clamp(flicker * 200, 16, 255);
            RendererUtils.PutPixelLocked(bmp, (int)p.X, (int)p.Y, intensity, intensity, (byte)(intensity + 20));
        }
    }
}
