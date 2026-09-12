using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Xstar.Music.App.Visualizer;

public sealed class StardustRenderer : IVisualizerRenderer
{
    public string StyleId => "stardust";
    public string DisplayName => "星尘";
    public bool IsHardwareCompatible => true;

    private WriteableBitmap? _bmp;
    private Particle[]? _particles;
    private readonly Random _rng = new(42);
    private float _beatFlash;

    private const int ParticleCount = 420;

    private struct Particle
    {
        public float X, Y;
        public float Vx, Vy;
        public float Size;
        public float Phase;
        public float Brightness;
    }

    public void Initialize(WriteableBitmap bitmap)
    {
        _bmp = bitmap;
        _particles = new Particle[ParticleCount];
        for (int i = 0; i < ParticleCount; i++)
        {
            _particles[i] = new Particle
            {
                X = (float)(_rng.NextDouble() * bitmap.PixelWidth),
                Y = (float)(_rng.NextDouble() * bitmap.PixelHeight),
                Vx = (float)((_rng.NextDouble() - 0.5) * 10),
                Vy = (float)((_rng.NextDouble() - 0.5) * 10),
                Size = (float)(_rng.NextDouble() * 1.8 + 0.4),
                Phase = (float)_rng.NextDouble() * MathF.PI * 2,
                Brightness = (float)_rng.NextDouble()
            };
        }
        _beatFlash = 0;
    }

    public void Render(VisualizerFrame frame)
    {
        if (_bmp is null || _particles is null) return;

        _bmp.Lock();
        try
        {
            int w = _bmp.PixelWidth, h = _bmp.PixelHeight;

            // Theme-tinted deep space + subtle starfield
            RendererUtils.ClearLocked(_bmp, frame.Background);

            var (baseHue, baseSat, _) = RendererUtils.RgbToHsl(frame.Accent);
            float bass = frame.BassEnergy;
            float mid = frame.MidEnergy;
            float treble = frame.TrebleEnergy;

            if (frame.BeatDetected) _beatFlash = 1;
            else _beatFlash = Math.Max(0, _beatFlash - 0.08f);

            float breathing = 0.6f + bass * 1.2f;
            float twinkleBoost = 0.3f + mid * 0.8f;
            float sparkleChance = treble * 0.08f;

            // Ambient nebula glow that breathes with the bass
            var nebCol = RendererUtils.HslToRgb((baseHue + 40) % 360, baseSat * 0.6, 0.5);
            RendererUtils.GlowLocked(_bmp, w / 2, h / 2, Math.Min(w, h) * 0.45f, nebCol.R, nebCol.G, nebCol.B, 0.06f + bass * 0.11f);

            var dimAccent = RendererUtils.HslToRgb(baseHue, baseSat * 0.5, 0.3);
            var brightAccent = RendererUtils.HslToRgb(baseHue, baseSat * 0.7, 0.85);
            var flashColor = RendererUtils.HslToRgb(baseHue, baseSat * 0.6, 0.95);

            for (int i = 0; i < _particles.Length; i++)
            {
                var p = _particles[i];

                p.Phase += 0.02f * twinkleBoost;
                float twinkle = 0.6f + 0.4f * (float)Math.Sin(p.Phase);

                p.X += p.Vx * (float)frame.DeltaTimeMs / 1000f * breathing;
                p.Y += p.Vy * (float)frame.DeltaTimeMs / 1000f * breathing;

                if (p.X < -6) p.X = w + 6;
                if (p.X > w + 6) p.X = -6;
                if (p.Y < -6) p.Y = h + 6;
                if (p.Y > h + 6) p.Y = -6;

                float bright = twinkle * (0.7f + _beatFlash * 0.5f);

                byte r = (byte)(dimAccent.R * (1 - bright) + brightAccent.R * bright);
                byte g = (byte)(dimAccent.G * (1 - bright) + brightAccent.G * bright);
                byte b = (byte)(dimAccent.B * (1 - bright) + brightAccent.B * bright);

                if (_beatFlash > 0.1f)
                {
                    r = (byte)(r + (flashColor.R - r) * _beatFlash * 0.5f);
                    g = (byte)(g + (flashColor.G - g) * _beatFlash * 0.5f);
                    b = (byte)(b + (flashColor.B - b) * _beatFlash * 0.5f);
                }

                int ix = (int)p.X, iy = (int)p.Y;
                float size = p.Size * breathing;

                if (size > 1.4f)
                {
                    // Bright large star → soft bloom
                    RendererUtils.GlowLocked(_bmp, ix, iy, size * 3f, r, g, b, 0.8f * bright);
                }
                int s = Math.Max(1, (int)(size * bright));
                RendererUtils.FillRectLocked(_bmp, ix - s / 2, iy - s / 2, s, s, r, g, b);

                if (_rng.NextDouble() < sparkleChance * 0.02)
                {
                    int fx = ix + _rng.Next(-8, 8);
                    int fy = iy + _rng.Next(-8, 8);
                    RendererUtils.GlowLocked(_bmp, fx, fy, 2f, brightAccent.R, brightAccent.G, brightAccent.B, 0.9f);
                }

                _particles[i] = p;
            }

            // A gentle bright core rising with the mid-energy
            var core = RendererUtils.HslToRgb(baseHue, baseSat, 0.9);
            RendererUtils.GlowLocked(_bmp, w / 2, h / 2, Math.Min(w, h) * 0.18f, core.R, core.G, core.B, mid * 0.10f);
        }
        finally { RendererUtils.FlushLocked(_bmp); }
    }

    public void Cleanup() { _bmp = null; _particles = null; }
}