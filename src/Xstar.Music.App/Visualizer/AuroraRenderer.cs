using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Xstar.Music.App.Visualizer;

public sealed class AuroraRenderer : IVisualizerRenderer
{
    public string StyleId => "aurora";
    public string DisplayName => "极光";
    public bool IsHardwareCompatible => true;

    private WriteableBitmap? _bmp;
    private readonly Random _rng = new(21);
    private double _phase;

    public void Initialize(WriteableBitmap bitmap)
    {
        _bmp = bitmap;
        _phase = 0;
    }

    public void Render(VisualizerFrame frame)
    {
        if (_bmp is null) return;

        _bmp.Lock();
        try
        {
            int w = _bmp.PixelWidth, h = _bmp.PixelHeight;

            RendererUtils.ClearLocked(_bmp, frame.Background);
            RendererUtils.DecayToLocked(_bmp, frame.Background, 0.75f);

            float bass = frame.BassEnergy;
            float mid = frame.MidEnergy;
            float treble = frame.TrebleEnergy;
            _phase += frame.DeltaTimeMs * 0.003f;

            var (baseHue, baseSat, _) = RendererUtils.RgbToHsl(frame.Accent);

            DrawAuroraBand(_bmp, w, h, 0, h * 0.3, 140, bass, baseHue, _phase * 0.6, baseSat);
            DrawAuroraBand(_bmp, w, h, 1, h * 0.5, 100, mid, (baseHue + 140) % 360, _phase * 0.9, baseSat);
            DrawAuroraBand(_bmp, w, h, 2, h * 0.72, 80, treble, (baseHue + 260) % 360, _phase * 1.2, baseSat);

            // Soft ambient auras so the bands read as luminous rather than flat.
            AmbientAura(_bmp, w, h * 0.3f, baseHue, bass);
            AmbientAura(_bmp, w, h * 0.5f, (baseHue + 140) % 360, mid);
            AmbientAura(_bmp, w, h * 0.72f, (baseHue + 260) % 360, treble);

            DrawStarField(_bmp, w, h, treble);
        }
        finally
        {
            RendererUtils.FlushLocked(_bmp);
        }
    }

    public void Cleanup() { _bmp = null; }

    private static void AmbientAura(WriteableBitmap bmp, int w, float bandCentre, double hue, float energy)
    {
        if (energy < 0.02f) return;
        var aura = RendererUtils.HslToRgb(hue, 0.75, 0.6);
        int midY = (int)bandCentre;
        RendererUtils.GlowLocked(bmp, w / 2, midY, bmp.PixelWidth * 0.30f, aura.R, aura.G, aura.B, 0.10f + energy * 0.10f);
    }

    private static void DrawAuroraBand(WriteableBitmap bmp, int w, int h, int bandIndex,
        double bandCentre, double bandThickness, float energy, double hueOffset, double phase, double sat)
    {
        if (energy < 0.02f) return;

        double hue = bandIndex switch
        {
            0 => hueOffset,
            1 => (hueOffset + 140) % 360,
            _ => (hueOffset + 260) % 360
        };

        double thickness = bandThickness * Math.Clamp(0.4f + energy * 1.8f, 0.2f, 2.2f);

        var bright = RendererUtils.HslToRgb(hue, sat * 0.95, 0.88);
        var mid = RendererUtils.HslToRgb(hue, sat * 0.7, 0.55);
        var dim = RendererUtils.HslToRgb(hue, sat * 0.5, 0.22);

        for (int x = 0; x < w; x++)
        {
            double t = (double)x / w;
            double shape = 0.5 * Math.Sin(t * 2.0 * Math.PI + phase)
                         + 0.3 * Math.Sin(t * 2.0 * Math.PI * 3.0 + phase * 1.4)
                         + 0.2 * Math.Sin(t * 2.0 * Math.PI * 5.0 + phase * 0.7);
            double bandY = bandCentre + shape * thickness * 0.4;

            for (int dy = -(int)thickness; dy <= (int)thickness; dy++)
            {
                double yy = bandY + dy;
                if (yy < 0 || yy >= h) continue;

                double falloff = Math.Exp(-dy * dy / (2 * (thickness * 0.4) * (thickness * 0.4)));
                if (falloff < 0.01) continue;
                double intensity = falloff * energy * 2.5;
                if (intensity < 0.02) continue;

                double dist = Math.Abs(dy) / thickness;
                byte rr = (byte)(mid.R * (1 - dist) + dim.R * dist * 0.7);
                byte gg = (byte)(mid.G * (1 - dist) + dim.G * dist * 0.7);
                byte bb = (byte)(mid.B * (1 - dist) + dim.B * dist * 0.7);

                if (Math.Abs(dy) < thickness * 0.2)
                {
                    double coreFalloff = Math.Exp(-dy * dy / (2 * (thickness * 0.1) * (thickness * 0.1)));
                    rr = (byte)(rr + bright.R * coreFalloff * intensity * 0.6);
                    gg = (byte)(gg + bright.G * coreFalloff * intensity * 0.6);
                    bb = (byte)(bb + bright.B * coreFalloff * intensity * 0.6);
                }

                rr = Math.Clamp(rr, (byte)0, (byte)255);
                gg = Math.Clamp(gg, (byte)0, (byte)255);
                bb = Math.Clamp(bb, (byte)0, (byte)255);

                RendererUtils.PutPixelLocked(bmp, x, (int)yy, rr, gg, bb);
            }
        }
    }

    private static readonly Random _starRng = new(21);
    private static Point[]? _starCache;

    private static void DrawStarField(WriteableBitmap bmp, int w, int h, float treble)
    {
        _starCache ??= Enumerable.Range(0, 80)
            .Select(_ => new Point(_starRng.Next(w), _starRng.Next(h)))
            .ToArray();

        foreach (var p in _starCache)
        {
            float flicker = (float)(0.4 + _starRng.NextDouble() * 0.6) * (0.6f + treble * 1.5f);
            if (flicker < 0.1f) continue;
            byte intensity = (byte)Math.Clamp(flicker * 220, 20, 255);
            RendererUtils.PutPixelLocked(bmp, (int)p.X, (int)p.Y, intensity, intensity, (byte)(intensity + 20));
        }
    }
}
