using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Xstar.Music.App.Visualizer;

/// <summary>
/// Beat-driven fireworks: shells burst at random points (or sweep left → right in rolling
/// barrages during intense passages) and spray additive sparks that fall under gravity.
/// Ported from docs/visualizer-fireworks.html.
/// </summary>
public sealed class FireworksRenderer : IVisualizerRenderer
{
    public string StyleId => "fireworks";
    public string DisplayName => "烟花";
    public bool IsHardwareCompatible => true;

    private const int MaxSparks = 3200;
    private const int MaxMicro = 1400;
    private const float Gravity = 78f;          // px / s^2
    private const float Tau = MathF.PI * 2f;

    private WriteableBitmap? _bmp;
    private readonly Random _rng = new(7);

    private readonly Spark[] _sparks = new Spark[MaxSparks];
    private int _sparkCount;
    private readonly Spark[] _micro = new Spark[MaxMicro];
    private int _microCount;

    private readonly List<Flash> _flashes = new();
    private readonly List<Pending> _pending = new();

    // Per-frame audio context (avoids threading it through every helper).
    private float _bass, _mid, _treble, _level, _accentHue;
    private float _timeSec, _nextBurstSec, _lastBarrageSec = -99f;
    private float _burstScale = 1f;

    private struct Spark
    {
        public float X, Y, Vx, Vy;
        public float Life, MaxLife;
        public float Hue, Sat, Light;
        public float Size, Drag, Grav, Tail;
        public float Blink, SplitAt;
        public int SplitCount;
        public byte Kind;        // 0 = normal, 1 = strobe, 2 = cross, 3 = frag
        public bool Crackle;
    }

    private struct Flash { public float X, Y, Life, MaxLife, Hue, Radius; }
    private struct Pending { public float At, X, Y, Hue, Scale; public Shell Type; }

    private enum Shell
    {
        Peony, Ring, DoubleRing, Willow, Chrysanthemum, Palm, Crossette, Strobe, Pistil,
        Crackle, Heart, Star, Spiral, MultiBreak, Saturn, Peacock, Comet, Dahlia, Wave,
        Butterfly, Brocade, TriRing
    }

    private static readonly (Shell Type, float Weight)[] ShellTable =
    {
        (Shell.Peony, 3.0f),
        (Shell.Ring, 2.0f),
        (Shell.DoubleRing, 1.6f),
        (Shell.Willow, 2.2f),
        (Shell.Chrysanthemum, 2.0f),
        (Shell.Palm, 1.4f),
        (Shell.Crossette, 1.5f),
        (Shell.Strobe, 1.5f),
        (Shell.Pistil, 1.6f),
        (Shell.Crackle, 1.3f),
        (Shell.Heart, 1.2f),
        (Shell.Star, 1.2f),
        (Shell.Spiral, 1.3f),
        (Shell.MultiBreak, 1.1f),
        (Shell.Saturn, 1.3f),
        (Shell.Peacock, 1.2f),
        (Shell.Comet, 1.2f),
        (Shell.Dahlia, 1.1f),
        (Shell.Wave, 1.2f),
        (Shell.Butterfly, 1.1f),
        (Shell.Brocade, 1.2f),
        (Shell.TriRing, 1.2f),
    };

    public void Initialize(WriteableBitmap bitmap)
    {
        _bmp = bitmap;
        _sparkCount = 0;
        _microCount = 0;
        _flashes.Clear();
        _pending.Clear();
        _timeSec = 0f;
        _nextBurstSec = 0f;
        _lastBarrageSec = -99f;
        _burstScale = 1f;

        bitmap.Lock();
        try { RendererUtils.ClearLocked(bitmap, RendererUtils.DefaultBackground); }
        finally { RendererUtils.FlushLocked(bitmap); }
    }

    public void Render(VisualizerFrame frame)
    {
        var bmp = _bmp;
        if (bmp is null) return;

        int w = bmp.PixelWidth, h = bmp.PixelHeight;
        float dt = Math.Clamp((float)frame.DeltaTimeMs / 1000f, 0f, 0.05f);

        _bass = frame.BassEnergy;
        _mid = frame.MidEnergy;
        _treble = frame.TrebleEnergy;
        _level = Math.Clamp(0.5f * _bass + 0.35f * _mid + 0.15f * _treble, 0f, 1f);
        _accentHue = (float)RendererUtils.RgbToHsl(frame.Accent).H;
        _timeSec += dt;

        bool hasAudio = frame.Spectrum.Length > 0 || frame.Waveform.Length > 0;

        bmp.Lock();
        try
        {
            // Per-frame trail fade toward the themed backdrop (canvas 'rgba(bg, 0.20)').
            // Slightly longer persistence than the prototype so strands glow.
            RendererUtils.DecayToLocked(bmp, frame.Background, 0.74f);

            DrawBackdropGlow(w, h);
            UpdatePending();
            if (hasAudio) Spawn(frame);
            UpdateSparks(dt, w, h);
            UpdateMicro(dt);
            UpdateFlashes(dt);

            DrawFlashes();
            DrawSparks(dt);
            DrawMicro(dt);
        }
        finally { RendererUtils.FlushLocked(bmp); }
    }

    public void Cleanup()
    {
        _bmp = null;
        _sparkCount = 0;
        _microCount = 0;
        _flashes.Clear();
        _pending.Clear();
    }

    /* ===================== spawn scheduling ===================== */

    private void UpdatePending()
    {
        for (int i = _pending.Count - 1; i >= 0; i--)
        {
            var p = _pending[i];
            if (p.At <= _timeSec)
            {
                _pending.RemoveAt(i);
                Explode(p.X, p.Y, p.Hue, p.Scale, p.Type);
            }
        }
    }

    private void Spawn(VisualizerFrame frame)
    {
        if (frame.BeatDetected)
        {
            ExplodeAtRandom();
            if (_level > 0.6f && Rand01() < 0.5f) ExplodeAtRandom();
        }

        if (_timeSec >= _nextBurstSec)
        {
            if (!frame.BeatDetected) ExplodeAtRandom();
            _nextBurstSec = _timeSec + (1.05f - 0.6f * _level) * Rand(0.8f, 1.4f);
        }

        // Rolling chain bursts during intense, fast passages.
        if (_timeSec - _lastBarrageSec > 3f)
        {
            bool chain = (frame.BeatDetected && _level > 0.55f && Rand01() < 0.45f)
                      || (_level > 0.78f && Rand01() < 0.15f);
            if (chain)
            {
                _lastBarrageSec = _timeSec;
                StartBarrage();
            }
        }
    }

    /// <summary>Queues a left → right sweep of bursts along a line or within a band.</summary>
    private void StartBarrage()
    {
        int w = _bmp!.PixelWidth, h = _bmp.PixelHeight;
        bool line = Rand01() < 0.5f;
        int count = 5 + _rng.Next(5);
        float stepTime = Rand(0.05f, 0.11f);
        float x0 = Rand(0.05f, 0.16f) * w;
        float x1 = Rand(0.84f, 0.95f) * w;
        float yBase = Rand(0.18f, 0.68f) * h;
        float bandH = line ? 0f : Rand(0.12f, 0.38f) * h;

        for (int i = 0; i < count; i++)
        {
            float f = count > 1 ? i / (float)(count - 1) : 0f;
            _pending.Add(new Pending
            {
                At = _timeSec + i * stepTime,
                X = x0 + (x1 - x0) * f,
                Y = line ? yBase : yBase + Rand(-0.5f, 0.5f) * bandH,
                Hue = BurstHue(),
                Scale = Rand(0.5f, 1.3f),
                Type = PickShell(),
            });
        }
    }

    private void ExplodeAtRandom()
        => Explode(Rand(0.12f, 0.88f) * _bmp!.PixelWidth,
                   Rand(0.12f, 0.80f) * _bmp.PixelHeight,
                   BurstHue());

    private Shell PickShell()
    {
        float total = 0f;
        foreach (var e in ShellTable) total += e.Weight;
        float r = Rand01() * total;
        foreach (var e in ShellTable) { r -= e.Weight; if (r <= 0f) return e.Type; }
        return Shell.Peony;
    }

    private float BurstHue()
    {
        float[] palette = { _accentHue, _accentHue + 150f, 45f, 330f, 168f, 275f };
        return Wrap(palette[_rng.Next(palette.Length)] + Rand(-18f, 18f));
    }

    /* ===================== shells ===================== */

    private void Explode(float x, float y, float hue, float scale = -1f, Shell? forced = null)
    {
        var type = forced ?? PickShell();

        // Vary the size / footprint of each burst: tiny pops up to huge brocades.
        if (scale < 0f)
        {
            scale = Rand(0.35f, 2.2f);
            if (Rand01() < 0.10f) scale *= Rand(1.3f, 1.6f);   // occasional monster
        }
        _burstScale = scale;

        int count = Math.Min(880, (int)((105f + _mid * 180f + _level * 150f) * scale));
        float speed = (95f + _bass * 235f) * MathF.Pow(scale, 0.8f);
        float rr = MathF.Min(_bmp!.PixelWidth, _bmp.PixelHeight) * 0.5f;

        switch (type)
        {
            case Shell.Ring:
                for (int i = 0; i < count; i++) Emit(With(Radial(x, y, i / (float)count * Tau, speed * Rand(0.9f, 1.04f), hue), tail: 1.8f));
                break;

            case Shell.DoubleRing:
            {
                int inner = (int)(count * 0.55f), outer = count - inner;
                for (int i = 0; i < outer; i++) Emit(With(Radial(x, y, i / (float)outer * Tau, speed * Rand(0.92f, 1.05f), hue), tail: 1.8f));
                for (int i = 0; i < inner; i++) Emit(With(Radial(x, y, i / (float)inner * Tau + 0.1f, speed * Rand(0.45f, 0.6f), hue + 150f), tail: 1.4f, light: Rand(70f, 92f)));
                break;
            }

            case Shell.Willow:
            {
                float gold = Wrap(hue * 0.25f + 38f);
                int c = Math.Max(18, Math.Min(count, (int)(count * 0.6f)));
                for (int i = 0; i < c; i++) Emit(With(Radial(x, y, Rand01() * Tau, speed * Rand(0.28f, 0.55f), gold),
                    maxLife: Rand(1.8f, 3.0f), drag: Rand(0.965f, 0.978f), grav: 1.7f, tail: 6f, size: Rand(1.1f, 2.0f), light: Rand(62f, 80f)));
                break;
            }

            case Shell.Chrysanthemum:
                for (int i = 0; i < count; i++) Emit(With(Radial(x, y, Rand01() * Tau, speed * MathF.Pow(Rand01(), 0.5f), hue),
                    tail: 3f, maxLife: Rand(1.1f, 2.2f), crackle: Rand01() < 0.35f));
                break;

            case Shell.Palm:
            {
                int strands = 12 + _rng.Next(10);
                for (int i = 0; i < strands; i++) Emit(With(Radial(x, y, i / (float)strands * Tau + Rand(-0.1f, 0.1f), speed * Rand(0.7f, 1.0f), hue),
                    size: Rand(2.6f, 4.2f), tail: 5f, grav: 1.4f, drag: Rand(0.94f, 0.965f), maxLife: Rand(1.5f, 2.4f),
                    kind: 3, splitAt: Rand(0.4f, 0.68f), splitCount: 3));
                break;
            }

            case Shell.Crossette:
                for (int i = 0; i < count; i++) Emit(With(Radial(x, y, Rand01() * Tau, speed * MathF.Pow(Rand01(), 0.5f), hue),
                    size: Rand(1.4f, 2.4f), tail: 2f, maxLife: Rand(0.9f, 1.4f), kind: 2, splitAt: Rand(0.35f, 0.6f), splitCount: 4));
                break;

            case Shell.Strobe:
                for (int i = 0; i < count; i++) Emit(With(Radial(x, y, Rand01() * Tau, speed * Rand(0.35f, 0.7f), hue),
                    kind: 1, blink: Rand(16f, 34f), maxLife: Rand(1.4f, 2.6f), size: Rand(1.2f, 2.0f), drag: Rand(0.93f, 0.96f)));
                break;

            case Shell.Pistil:
            {
                int core = (int)(count * 0.4f), outN = count - core;
                for (int i = 0; i < outN; i++) Emit(With(Radial(x, y, Rand01() * Tau, speed * MathF.Pow(Rand01(), 0.5f), hue), tail: 2f));
                for (int i = 0; i < core; i++) Emit(With(Radial(x, y, Rand01() * Tau, speed * Rand(0.35f, 0.6f), hue),
                    tail: 1.6f, light: Rand(88f, 100f), sat: Rand(10f, 40f), size: Rand(1.6f, 2.6f)));
                break;
            }

            case Shell.Crackle:
                for (int i = 0; i < count; i++) Emit(With(Radial(x, y, Rand01() * Tau, speed * MathF.Pow(Rand01(), 0.6f), hue),
                    crackle: true, maxLife: Rand(0.9f, 1.7f), size: Rand(1.0f, 2.0f)));
                break;

            case Shell.Heart:
            {
                int c = Math.Min(count, 340);
                for (int i = 0; i < c; i++)
                {
                    float tt = i / (float)c * Tau;
                    float hx = 16f * MathF.Pow(MathF.Sin(tt), 3f);
                    float hy = -(13f * MathF.Cos(tt) - 5f * MathF.Cos(2f * tt) - 2f * MathF.Cos(3f * tt) - MathF.Cos(4f * tt));
                    float mag = MathF.Sqrt(hx * hx + hy * hy) / 17f;
                    Emit(With(Radial(x, y, MathF.Atan2(hy, hx), speed * mag, hue), tail: 1.8f, maxLife: Rand(1.0f, 1.9f)));
                }
                break;
            }

            case Shell.Star:
            {
                const int points = 5, segs = points * 2;
                int c = Math.Min(count, 360);
                for (int i = 0; i < c; i++)
                {
                    float f = i / (float)c * segs;
                    int idx = (int)MathF.Floor(f);
                    int s0 = idx % segs, s1 = (s0 + 1) % segs;
                    float ft = f - idx;
                    float ax = -MathF.PI / 2f + s0 * MathF.PI / points, ar = s0 % 2 == 0 ? 1f : 0.42f;
                    float bx = -MathF.PI / 2f + s1 * MathF.PI / points, br = s1 % 2 == 0 ? 1f : 0.42f;
                    float px = MathF.Cos(ax) * ar + (MathF.Cos(bx) * br - MathF.Cos(ax) * ar) * ft;
                    float py = MathF.Sin(ax) * ar + (MathF.Sin(bx) * br - MathF.Sin(ax) * ar) * ft;
                    Emit(With(Radial(x, y, MathF.Atan2(py, px), speed * MathF.Sqrt(px * px + py * py), hue), tail: 1.6f, maxLife: Rand(0.9f, 1.7f)));
                }
                break;
            }

            case Shell.Spiral:
            {
                const int arms = 3;
                int c = Math.Min(count, 380);
                for (int i = 0; i < c; i++)
                {
                    float tt = i / (float)c;
                    float ang = tt * MathF.PI * 3.4f + (i % arms) * (Tau / arms);
                    Emit(With(Radial(x, y, ang, speed * (0.25f + tt * 0.85f), hue + (i % arms) * 40f), tail: 3f, maxLife: Rand(1.0f, 2.0f)));
                }
                break;
            }

            case Shell.MultiBreak:
            {
                int sub = 3 + _rng.Next(3);
                Shell[] types = { Shell.Peony, Shell.Ring, Shell.Strobe, Shell.Crackle, Shell.Pistil, Shell.Chrysanthemum };
                for (int i = 0; i < sub; i++)
                {
                    float a = Rand01() * Tau;
                    float d = rr * Rand(0.12f, 0.32f) * scale;
                    _pending.Add(new Pending
                    {
                        At = _timeSec + Rand(0.10f, 0.45f),
                        X = x + MathF.Cos(a) * d,
                        Y = y + MathF.Sin(a) * d,
                        Hue = Wrap(hue + Rand(-40f, 40f)),
                        Scale = scale * Rand(0.4f, 0.65f),
                        Type = types[_rng.Next(types.Length)],
                    });
                }
                break;
            }

            case Shell.Saturn:
            {
                int c = Math.Min(count, 320);
                int coreN = (int)(c * 0.6f);
                for (int i = 0; i < coreN; i++) Emit(With(Radial(x, y, Rand01() * Tau, speed * MathF.Pow(Rand01(), 0.5f), hue), tail: 1.5f));

                int ringN = c - coreN;
                float tilt = Rand(-0.5f, 0.5f), ct = MathF.Cos(tilt), st = MathF.Sin(tilt);
                for (int i = 0; i < ringN; i++)
                {
                    float a = i / (float)ringN * Tau;
                    float sp = speed * Rand(0.95f, 1.06f);
                    float ex = MathF.Cos(a) * sp, ey = MathF.Sin(a) * sp * 0.22f;
                    var s = default(Spark);
                    s.X = x; s.Y = y;
                    s.Vx = ex * ct - ey * st;
                    s.Vy = ex * st + ey * ct;
                    s.Hue = Wrap(hue + 155f + Rand(-15f, 15f));
                    s.Sat = Rand(85f, 100f); s.Light = Rand(70f, 90f);
                    s.Size = Rand(1.1f, 2.0f); s.Drag = Rand(0.9f, 0.95f);
                    s.Grav = 1f; s.Tail = 2.5f; s.MaxLife = Rand(1.1f, 1.9f);
                    s.Crackle = Rand01() < _treble * 0.2f;
                    Store(s);
                }
                break;
            }

            case Shell.Peacock:
            {
                int c = Math.Min(count, 340);
                for (int i = 0; i < c; i++)
                {
                    float f = i / (float)c;
                    float ang = -MathF.PI * 0.85f + f * MathF.PI * 0.7f;
                    float sp = speed * (0.5f + 0.5f * MathF.Abs(MathF.Sin(f * MathF.PI * 3f)));
                    Emit(With(Radial(x, y, ang, sp, hue + f * 70f), tail: 3f, maxLife: Rand(1.0f, 1.9f)));
                }
                break;
            }

            case Shell.Comet:
            {
                float dir = Rand(-MathF.PI, 0f);
                int c = Math.Min(count, 260);
                for (int i = 0; i < c; i++)
                {
                    float f = i / (float)c;
                    Emit(With(Radial(x, y, dir + Rand(-0.06f, 0.06f), speed * (1f - f * 0.65f) + 12f, hue),
                        tail: 5f, size: Rand(1.0f, 2.2f), maxLife: Rand(1.2f, 2.1f)));
                }
                break;
            }

            case Shell.Dahlia:
            {
                int c = Math.Max(36, Math.Min(count, 150));
                for (int i = 0; i < c; i++) Emit(With(Radial(x, y, Rand01() * Tau, speed * Rand(0.6f, 1.0f), hue),
                    tail: 5f, size: Rand(1.6f, 3.0f), maxLife: Rand(1.4f, 2.4f)));
                break;
            }

            case Shell.Wave:
            {
                int c = Math.Min(count, 380);
                for (int i = 0; i < c; i++)
                {
                    float a = i / (float)c * Tau;
                    float sp = speed * (0.45f + 0.55f * MathF.Abs(MathF.Cos(a * 2.5f)));
                    Emit(With(Radial(x, y, a, sp, hue), tail: 2f));
                }
                break;
            }

            case Shell.Butterfly:
            {
                int c = Math.Min(count, 340);
                float off = speed * 0.32f;
                for (int i = 0; i < c; i++)
                {
                    float side = (i & 1) == 1 ? 1f : -1f;
                    float a = Rand01() * Tau;
                    float sp = speed * Rand(0.25f, 0.55f);
                    var s = default(Spark);
                    s.X = x; s.Y = y;
                    s.Vx = MathF.Cos(a) * sp + side * off;
                    s.Vy = MathF.Sin(a) * sp * 0.7f;
                    s.Hue = Wrap(hue + Rand(-20f, 20f));
                    s.Sat = Rand(85f, 100f); s.Light = Rand(60f, 85f);
                    s.Size = Rand(1.0f, 2.0f); s.Drag = Rand(0.9f, 0.95f);
                    s.Grav = 1f; s.Tail = 2.2f; s.MaxLife = Rand(1.1f, 2.0f);
                    s.Crackle = Rand01() < _treble * 0.2f;
                    Store(s);
                }
                break;
            }

            case Shell.Brocade:
            {
                float gold = Wrap(hue * 0.3f + 42f);
                int c = Math.Min(count, 520);
                for (int i = 0; i < c; i++) Emit(With(Radial(x, y, Rand01() * Tau, speed * MathF.Pow(Rand01(), 0.5f), gold),
                    tail: 4f, size: Rand(1.2f, 2.4f), drag: Rand(0.95f, 0.972f), grav: 1.35f, maxLife: Rand(1.4f, 2.5f), crackle: Rand01() < 0.5f));
                break;
            }

            case Shell.TriRing:
            {
                float[] radii = { 1f, 0.72f, 0.45f };
                float[] hues = { hue, hue + 120f, hue + 240f };
                for (int ring = 0; ring < 3; ring++)
                {
                    int c = Math.Max(24, count / 3);
                    for (int i = 0; i < c; i++) Emit(With(Radial(x, y, i / (float)c * Tau, speed * radii[ring] * Rand(0.97f, 1.03f), hues[ring]), tail: 1.8f));
                }
                break;
            }

            default: // Peony
                for (int i = 0; i < count; i++) Emit(Radial(x, y, Rand01() * Tau, speed * MathF.Pow(Rand01(), 0.55f), hue));
                break;
        }

        _flashes.Add(new Flash
        {
            X = x, Y = y, Life = 0f, MaxLife = Rand(0.24f, 0.34f), Hue = hue,
            Radius = rr * (0.08f + Rand01() * 0.05f) + _bass * rr * 0.12f,
        });
    }

    /// <summary>Builds a radial spark with the prototype's default jitter.</summary>
    private Spark Radial(float x, float y, float ang, float sp, float hue) => new()
    {
        X = x,
        Y = y,
        Vx = MathF.Cos(ang) * sp,
        Vy = MathF.Sin(ang) * sp,
        Life = 0f,
        MaxLife = Rand(0.9f, 1.8f),
        Hue = Wrap(hue + Rand(-26f, 26f)),
        Sat = Rand(82f, 100f),
        Light = Rand(56f, 82f),
        Size = Rand(0.9f, 2.2f),
        Drag = Rand(0.86f, 0.955f),
        Grav = 1f,
        Tail = 1f,
        Crackle = Rand01() < _treble * 0.18f,
    };

    // Optional per-field overrides applied on top of a Radial() spark.
    private static Spark With(Spark s, float tail = 1f, float maxLife = 0f, float drag = 0f, float grav = 1f,
        float size = 0f, float light = 0f, float sat = 0f, float blink = 0f, float splitAt = 0f,
        int splitCount = 0, byte kind = 0, bool crackle = false)
    {
        s.Tail = tail;
        if (maxLife > 0f) s.MaxLife = maxLife;
        if (drag > 0f) s.Drag = drag;
        s.Grav = grav;
        if (size > 0f) s.Size = size;
        if (light > 0f) s.Light = light;
        if (sat > 0f) s.Sat = sat;
        s.Blink = blink;
        s.SplitAt = splitAt;
        s.SplitCount = splitCount;
        s.Kind = kind;
        if (crackle) s.Crackle = true;
        return s;
    }

    /// <summary>Scales size by the current burst scale (matches the prototype's radial()).</summary>
    private void Emit(Spark s)
    {
        s.Size = MathF.Min(8f, s.Size * _burstScale);
        Store(s);
    }

    private void Store(Spark s)
    {
        if (_sparkCount >= MaxSparks) return;
        s.Hue = Wrap(s.Hue);
        _sparks[_sparkCount++] = s;
    }

    /* ===================== simulation ===================== */

    private void UpdateSparks(float dt, int w, int h)
    {
        for (int i = _sparkCount - 1; i >= 0; i--)
        {
            var s = _sparks[i];
            s.Life += dt;

            // Fragmentation / crossette shells split into children mid-flight.
            if (s.SplitAt > 0f && s.Life >= s.MaxLife * s.SplitAt)
            {
                float baseSpeed = MathF.Sqrt(s.Vx * s.Vx + s.Vy * s.Vy);
                float ang0 = MathF.Atan2(s.Vy, s.Vx);
                int k = s.SplitCount > 0 ? s.SplitCount : 4;
                float spread = s.Kind == 2 ? 0.72f : 0.55f;
                for (int j = 0; j < k; j++)
                {
                    float a = ang0 + j * (Tau / k);
                    Store(new Spark
                    {
                        X = s.X, Y = s.Y,
                        Vx = MathF.Cos(a) * baseSpeed * spread,
                        Vy = MathF.Sin(a) * baseSpeed * spread,
                        Life = 0f, MaxLife = Rand(0.4f, 0.9f),
                        Hue = s.Hue, Sat = s.Sat, Light = MathF.Min(96f, s.Light + 12f),
                        Size = s.Size * 0.75f, Drag = s.Drag, Grav = s.Grav, Tail = s.Tail * 0.6f,
                        Crackle = Rand01() < _treble * 0.3f,
                    });
                }
                _sparks[i] = _sparks[--_sparkCount];
                continue;
            }

            if (s.Life >= s.MaxLife) { _sparks[i] = _sparks[--_sparkCount]; continue; }

            float drag = MathF.Pow(s.Drag, dt * 60f);
            s.Vx *= drag;
            s.Vy = s.Vy * drag + Gravity * s.Grav * dt;
            s.X += s.Vx * dt;
            s.Y += s.Vy * dt;

            if (s.X < -60f || s.X > w + 60f || s.Y < -60f || s.Y > h + 60f)
            {
                _sparks[i] = _sparks[--_sparkCount];
                continue;
            }

            if (s.Crackle && s.Life > s.MaxLife * 0.55f && Rand01() < _treble * 0.5f) SpawnCrackle(in s);
            _sparks[i] = s;
        }
    }

    private void SpawnCrackle(in Spark parent)
    {
        int n = 1 + _rng.Next(2);
        for (int i = 0; i < n; i++)
        {
            if (_microCount >= MaxMicro) return;
            float ang = Rand01() * Tau;
            float sp = Rand(25f, 80f);
            _micro[_microCount++] = new Spark
            {
                X = parent.X, Y = parent.Y,
                Vx = MathF.Cos(ang) * sp, Vy = MathF.Sin(ang) * sp,
                Life = 0f, MaxLife = Rand(0.18f, 0.4f),
                Hue = parent.Hue, Sat = parent.Sat, Light = 88f,
                Size = 0.8f, Drag = 0.9f, Grav = 1f, Tail = 1f,
            };
        }
    }

    private void UpdateMicro(float dt)
    {
        for (int i = _microCount - 1; i >= 0; i--)
        {
            var s = _micro[i];
            s.Life += dt;
            if (s.Life >= s.MaxLife) { _micro[i] = _micro[--_microCount]; continue; }
            float drag = MathF.Pow(0.9f, dt * 60f);
            s.Vx *= drag;
            s.Vy = s.Vy * drag + Gravity * dt;
            s.X += s.Vx * dt;
            s.Y += s.Vy * dt;
            _micro[i] = s;
        }
    }

    private void UpdateFlashes(float dt)
    {
        for (int i = _flashes.Count - 1; i >= 0; i--)
        {
            var f = _flashes[i];
            f.Life += dt;
            if (f.Life >= f.MaxLife) { _flashes.RemoveAt(i); continue; }
            _flashes[i] = f;
        }
    }

    /* ===================== drawing ===================== */

    private void DrawBackdropGlow(int w, int h)
    {
        // Ambient themed glow that breathes with the bass (mirrors the prototype's
        // per-frame radial backdrop, which the port had omitted).
        float minDim = MathF.Min(w, h);
        var c = RendererUtils.HslToRgb(_accentHue + 30f, 0.7, 0.45);
        RendererUtils.GlowLocked(_bmp!, w / 2, (int)(h * 0.55f), minDim * 0.6f, c.R, c.G, c.B, 0.05f + _bass * 0.06f, 2);
    }

    private void DrawFlashes()
    {
        var bmp = _bmp!;
        foreach (var f in _flashes)
        {
            float t = f.Life / f.MaxLife;
            float a = (1f - t) * (1f - t);
            float radius = f.Radius * (0.6f + t * 1.6f);

            // coloured halo + white-hot core for a satisfying pop
            var halo = RendererUtils.HslToRgb(f.Hue, 0.85, 0.62);
            RendererUtils.GlowLocked(bmp, (int)f.X, (int)f.Y, radius, halo.R, halo.G, halo.B, a * 0.85f, 2);
            var core = RendererUtils.HslToRgb(f.Hue, 0.35, 0.96);
            RendererUtils.GlowLocked(bmp, (int)f.X, (int)f.Y, radius * 0.45f, core.R, core.G, core.B, a * 0.95f);
        }
    }

    private void DrawSparks(float dt)
    {
        var bmp = _bmp!;
        for (int i = 0; i < _sparkCount; i++)
        {
            var s = _sparks[i];
            float t = s.Life / s.MaxLife;
            float a = (1f - t) * (0.75f + 0.25f * MathF.Sin(s.Life * 40f + s.Hue));
            if (s.Kind == 1) a *= MathF.Sin(s.Life * s.Blink) > 0f ? 1f : 0.12f;
            if (a <= 0.01f) continue;

            float tailK = MathF.Max(1f, s.Tail);
            float tx = s.X - s.Vx * dt * tailK;
            float ty = s.Y - s.Vy * dt * tailK;
            float ddx = s.X - tx, ddy = s.Y - ty;
            float len = MathF.Sqrt(ddx * ddx + ddy * ddy);
            if (len > 70f)
            {
                float k = 70f / len;
                tx = s.X - ddx * k;
                ty = s.Y - ddy * k;
            }

            float width = s.Size * (1f - t * 0.45f);
            var c = RendererUtils.HslToRgb(s.Hue, s.Sat / 100.0, s.Light / 100.0);
            DrawTrail(bmp, tx, ty, s.X, s.Y, c, a, width);

            // comet-like bright head, brighter than the strand colour
            if (width > 1.6f)
            {
                var core = RendererUtils.HslToRgb(s.Hue, Math.Min(100.0, s.Sat + 10.0) / 100.0, Math.Min(100.0, s.Light + 22.0) / 100.0);
                RendererUtils.GlowLocked(bmp, (int)s.X, (int)s.Y, MathF.Max(2.5f, width * 1.7f), core.R, core.G, core.B, a * 0.6f);
            }
        }
    }

    /// <summary>
    /// Additive spark streak: a bright core plus parallel layers whose count grows with
    /// <paramref name="width"/>, so large shells read as fat glowing strands instead of
    /// thin 1px lines.
    /// </summary>
    private static void DrawTrail(WriteableBitmap bmp, float x0, float y0, float x1, float y1, Color c, float intensity, float width)
    {
        RendererUtils.DrawSoftLineAddLocked(bmp, x0, y0, c, x1, y1, c, intensity * 1.15f);
        if (width <= 1.9f) return;

        float dx = x1 - x0, dy = y1 - y0;
        float len = MathF.Sqrt(dx * dx + dy * dy);
        if (len < 0.001f) len = 1f;
        float nx = -dy / len, ny = dx / len;

        int layers = Math.Min(2, (int)((width - 1f) * 0.5f));
        for (int i = 1; i <= layers; i++)
        {
            float o = i * 1.6f;
            float a = intensity * (0.45f / i);
            RendererUtils.DrawSoftLineAddLocked(bmp, x0 + nx * o, y0 + ny * o, c, x1 + nx * o, y1 + ny * o, c, a);
            RendererUtils.DrawSoftLineAddLocked(bmp, x0 - nx * o, y0 - ny * o, c, x1 - nx * o, y1 - ny * o, c, a);
        }
    }

    private void DrawMicro(float dt)
    {
        var bmp = _bmp!;
        for (int i = 0; i < _microCount; i++)
        {
            var s = _micro[i];
            float a = 1f - s.Life / s.MaxLife;
            var c = RendererUtils.HslToRgb(s.Hue, 1.0, 0.92);
            RendererUtils.DrawSoftLineAddLocked(bmp, s.X - s.Vx * dt, s.Y - s.Vy * dt, c, s.X, s.Y, c, a);
        }
    }

    /* ===================== helpers ===================== */

    private float Rand(float a, float b) => (float)(a + _rng.NextDouble() * (b - a));
    private float Rand01() => (float)_rng.NextDouble();
    private static float Wrap(float h) { h %= 360f; if (h < 0f) h += 360f; return h; }
}
