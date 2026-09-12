using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Xstar.Music.App.Visualizer;

public sealed class GalaxyRenderer : IVisualizerRenderer
{
    public string StyleId => "galaxy";
    public string DisplayName => "银河";
    public bool IsHardwareCompatible => true;

    private WriteableBitmap? _bmp;
    private SpiralParticle[]? _particles;
    private readonly Random _rng = new(7);
    private double _rotation;

    private const int SpiralCount = 4;
    private const int ParticlesPerSpiral = 90;
    private const int TotalParticles = SpiralCount * ParticlesPerSpiral;

    private struct SpiralParticle
    {
        public float Radius;
        public float AngleOffset;
        public float RadialOffset;
        public float T;
    }

    public void Initialize(WriteableBitmap bitmap)
    {
        _bmp = bitmap;
        _particles = new SpiralParticle[TotalParticles];
        int idx = 0;
        for (int s = 0; s < SpiralCount; s++)
        {
            double armAngleOffset = (double)s / SpiralCount * Math.PI * 2;
            for (int p = 0; p < ParticlesPerSpiral; p++)
            {
                float t = (float)p / ParticlesPerSpiral;
                double theta = t * Math.PI * 3.5;
                double r = 20 * Math.Exp(theta * 0.35);
                _particles[idx++] = new SpiralParticle
                {
                    Radius = (float)r,
                    AngleOffset = (float)armAngleOffset,
                    RadialOffset = (float)(_rng.NextDouble() * 14 - 7),
                    T = t
                };
            }
        }
        _rotation = 0;
    }

    public void Render(VisualizerFrame frame)
    {
        if (_bmp is null || _particles is null) return;

        _bmp.Lock();
        try
        {
            int w = _bmp.PixelWidth, h = _bmp.PixelHeight;
            int cx = w / 2, cy = h / 2;

            // Theme-tinted backdrop with a soft decay trail for the motion smear.
            RendererUtils.ClearLocked(_bmp, frame.Background);

            float bass = frame.BassEnergy;
            float mid = frame.MidEnergy;
            float treble = frame.TrebleEnergy;
            bool beat = frame.BeatDetected;

            double baseRotSpeed = 0.00015 + bass * 0.0011;
            _rotation += frame.DeltaTimeMs * baseRotSpeed;

            float coreExpand = 1f + bass * 0.7f + (beat ? 0.35f : 0f);

            var (hue, sat, _) = RendererUtils.RgbToHsl(frame.Accent);
            double violetHue = (hue + 60) % 360;
            var armHot = RendererUtils.HslToRgb(violetHue, sat, 0.9);
            var armMid = RendererUtils.HslToRgb(violetHue, sat * 0.7, 0.55);
            var armDim = RendererUtils.HslToRgb(violetHue, sat * 0.5, 0.25);
            var core = RendererUtils.HslToRgb(violetHue, sat, 0.92);

            int maxR = Math.Min(w, h) / 2 - 30;

            // Spiral arms with additive glow
            for (int i = 0; i < _particles.Length; i++)
            {
                var p = _particles[i];
                float r = (p.Radius + p.RadialOffset) * coreExpand;
                if (r > maxR) continue;

                double angle = p.AngleOffset + _rotation * (0.5 + p.T * 0.5);
                int x = cx + (int)(Math.Cos(angle) * r);
                int y = cy + (int)(Math.Sin(angle) * r);

                float t = p.T;
                byte rc, gc, bc;
                if (t < 0.4f)
                {
                    float f = t / 0.4f;
                    rc = (byte)(armHot.R * (1 - f) + armMid.R * f);
                    gc = (byte)(armHot.G * (1 - f) + armMid.G * f);
                    bc = (byte)(armHot.B * (1 - f) + armMid.B * f);
                }
                else
                {
                    float f2 = (t - 0.4f) / 0.6f;
                    rc = (byte)(armMid.R * (1 - f2) + armDim.R * f2);
                    gc = (byte)(armMid.G * (1 - f2) + armDim.G * f2);
                    bc = (byte)(armMid.B * (1 - f2) + armDim.B * f2);
                }

                float brightnessBoost = 0.5f + mid * 0.7f + treble * 0.3f;
                int size = Math.Max(1, (int)((0.6 + p.T * 2.5f) * brightnessBoost));

                // Every arm particle blooms softly
                RendererUtils.GlowLocked(_bmp, x, y, size * 1.8f, rc, gc, bc, 0.55f);
                RendererUtils.FillRectLocked(_bmp, x - size / 2, y - size / 2, size, size, rc, gc, bc);
            }

            // Radiant galactic core
            int coreSize = (int)(26 * coreExpand);
            RendererUtils.GlowLocked(_bmp, cx, cy, coreSize * 2.2f, core.R, core.G, core.B, 0.9f);
            RendererUtils.GlowLocked(_bmp, cx, cy, coreSize, 255, 255, 255, 0.5f);
            RendererUtils.FillRectLocked(_bmp, cx - coreSize / 2, cy - coreSize / 2, coreSize, coreSize, core.R, core.G, core.B);
        }
        finally { RendererUtils.FlushLocked(_bmp); }
    }

    public void Cleanup() { _bmp = null; _particles = null; }
}