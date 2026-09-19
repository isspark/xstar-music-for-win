using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Xstar.Music.App.Visualizer;

/// <summary>
/// 平铺山脉波形：把“圆形”环形山脉横向铺平——低频聚于屏幕中央隆起主山脉，
/// 高频锯齿峰坡向两侧，屏幕两端自然落入山谷。连续填充的山体从地平线隆起，
/// 山麓近黑、山脊发光、峰顶泛白雪辉光；后方一层暗淡远山拉开纵深，
/// 两个光点沿海脊线游走，beat 时向上升起暖色火星。帧间衰减留下等高线光迹。
/// </summary>
public sealed class WaveRenderer : IVisualizerRenderer
{
    public string StyleId => "wave";
    public string DisplayName => "波形";
    public bool IsHardwareCompatible => true;

    private const int Slices = 400;
    private const int SparkCount = 48;

    private WriteableBitmap? _bmp;
    private readonly MountainSpectrum _spectrum = new();
    private readonly float[] _envelope = new float[Slices];
    private readonly Random _rng = new(21);
    private readonly Spark[] _sparks = new Spark[SparkCount];
    private double _hueShift;
    private double _starPhase;
    private float[]? _slow;

    private struct Spark
    {
        public float X, Y, Vx, Vy, Life, MaxLife;
        public double Hue;
    }

    public void Initialize(WriteableBitmap bitmap)
    {
        _bmp = bitmap;
        _hueShift = 0;
        _starPhase = 0;
        _slow = null;
        Array.Clear(_envelope);
        for (int i = 0; i < SparkCount; i++) _sparks[i].Life = 0;

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

            // 帧间衰减 → 峰尖退落时留下等高线般的渐隐光迹（融入主题背景色）
            RendererUtils.DecayToLocked(_bmp, frame.Background, 0.80f);

            var spectrum = frame.Spectrum;
            if (spectrum.Length == 0) return;

            float dt = (float)frame.DeltaTimeMs / 1000f;
            _hueShift += frame.DeltaTimeMs * 0.02;
            _starPhase += dt;
            bool beat = frame.BeatDetected;
            float beatBoost = beat ? 1.08f : 1f;

            // 与圆形模式完全相同的山脉包络
            var mt = _spectrum.Update(spectrum);
            MountainSpectrum.BuildEnvelope(mt, _envelope, lowAtEnds: false);

            // 全局自动增益：满编段落整体压低、稀疏段落推起，
            // 让高潮时山脊也保持起伏而不是连成一条平顶线。
            float mean = 0f;
            for (int s = 0; s < Slices; s++) mean += _envelope[s];
            mean /= Slices;
            float gain = 1f / (0.72f + 0.45f * mean);

            // 逐柱瞬态塑造：每根山脊跟随一个“响度惯性”（快攻击、慢释放），
            // 持续响度沉为低矮缓坡，超出惯性的瞬时冲击（鼓点 / 旋律起伏）才
            // 隆起成尖峰——中央常驻低频因此不再盖成平顶山脉。
            float[] slow = _slow ??= new float[Slices];
            for (int s = 0; s < Slices; s++)
            {
                float e = Math.Clamp(_envelope[s] * gain, 0f, 1f);
                slow[s] += (e - slow[s]) * (e > slow[s] ? 0.15f : 0.05f);
                float line = slow[s] * 0.72f;
                float nov = Math.Clamp((e - line) / Math.Max(0.18f, 1f - line), 0f, 1f);
                float height = e * 0.28f + nov * 0.72f;
                _envelope[s] = MathF.Pow(Math.Clamp(height, 0f, 1f), 1.25f);
            }

            // 地平线 / 山脊几何
            int groundY = (int)(h * 0.90);
            float peakH = h * 0.62f;
            int farY = (int)(h * 0.74);
            float farH = h * 0.10f;

            var (accentHue, accentSat, _) = RendererUtils.RgbToHsl(frame.Accent);

            // 氛围辉光（冷色角落光 + 地平线中央强调色微光）
            var corner = RendererUtils.HslToRgb(235, 0.8, 0.4);
            RendererUtils.GlowLocked(_bmp, (int)(w * 0.08), (int)(h * 0.10), h * 0.42f,
                corner.R, corner.G, corner.B, 0.05f, 3);
            RendererUtils.GlowLocked(_bmp, (int)(w * 0.92), (int)(h * 0.10), h * 0.42f,
                corner.R, corner.G, corner.B, 0.05f, 3);
            var horizonGlow = RendererUtils.HslToRgb(accentHue, accentSat, 0.5);
            RendererUtils.GlowLocked(_bmp, w / 2, groundY, h * 0.55f,
                horizonGlow.R, horizonGlow.G, horizonGlow.B, 0.06f, 4);

            // ── 远山剪影（先画，暗淡低矮）─────────────────────────────────
            DrawRange(_bmp, w, farY, farH, 0.55f, far: true);

            // ── 主山脉连续填充 ────────────────────────────────────────────
            DrawRange(_bmp, w, groundY, peakH, 1f, far: false);

            // ── 山脊亮线 + 雪顶辉光 ────────────────────────────────────────
            for (int s = 0; s < Slices; s++)
            {
                float v0 = _envelope[s], v1 = _envelope[(s + 1) % Slices];
                if (s == Slices - 1) v1 = v0; // 两端不跨界相连
                if (v0 < 0.015f) continue;

                double h0 = SliceHue(s);
                float prev = s > 0 ? _envelope[s - 1] : v0;
                float next = v1;
                var c0 = MountainSpectrum.RidgeColor(h0, v0, prev, next);

                int x0 = s * w / Slices;
                int x1 = (s + 1) * w / Slices;
                int y0 = groundY - (int)(v0 * peakH * beatBoost);
                int y1 = groundY - (int)(v1 * peakH * beatBoost);

                if (v1 >= 0.015f)
                {
                    var c1 = MountainSpectrum.RidgeColor(SliceHue(s + 1), v1, v0,
                        s + 2 < Slices ? _envelope[s + 2] : v1);
                    RendererUtils.DrawLineLocked(_bmp, x0, y0, c0, x1, y1, c1);
                }
                else
                {
                    RendererUtils.FillRectLocked(_bmp, x0, y0, 1, 1, c0.R, c0.G, c0.B);
                }

                if ((s & 3) == 0)
                    RendererUtils.GlowLocked(_bmp, x0, y0, 2.6f, c0.R, c0.G, c0.B, 0.28f);
                bool peak = s > 0 && s < Slices - 1 && v0 > v1 && v0 >= prev && v0 > 0.22f;
                if (peak)
                    RendererUtils.GlowLocked(_bmp, x0, y0, 6f, c0.R, c0.G, c0.B, 0.55f + 0.4f * v0);
            }

            // ── 地平线光带 ────────────────────────────────────────────────
            var groundHot = RendererUtils.HslToRgb(accentHue, accentSat, 0.75);
            for (int x = 0; x < w; x += 3)
                RendererUtils.GlowLocked(_bmp, x, groundY, 2.2f, groundHot.R, groundHot.G, groundHot.B, 0.22f, 2);

            // ── 两个光点沿山脊游走 ────────────────────────────────────────
            DrawTravelStar(_starPhase * 0.07f, 165, w, groundY, peakH, beatBoost);
            DrawTravelStar(1f - (_starPhase * 0.055f + 0.32f) % 1f, 315, w, groundY, peakH, beatBoost);

            // ── beat 火星（自峰顶向上升起）────────────────────────────────
            if (beat) SpawnSparks(w, groundY, peakH);
            UpdateSparks(dt);
        }
        finally { RendererUtils.FlushLocked(_bmp); }
    }

    private double SliceHue(int slice)
        => (slice / (double)Slices * 360.0 + _hueShift) % 360;

    /// <summary>连续楔形填充一整排山体（far=true 时为暗淡远山）。</summary>
    private void DrawRange(WriteableBitmap bmp, int w, int baseY, float height, float gain, bool far)
    {
        Span<RendererUtils.PolyVertex> quad = stackalloc RendererUtils.PolyVertex[4];
        for (int s = 0; s < Slices; s++)
        {
            float v0 = _envelope[s] * gain;
            float v1 = (s + 1 < Slices ? _envelope[s + 1] : v0) * gain;
            if (v0 + v1 < 0.02f) continue;

            double hue0 = SliceHue(s), hue1 = SliceHue(s + 1);
            Color foot0, foot1, top0, top1;
            if (far)
            {
                foot0 = RendererUtils.HslToRgb(hue0, 0.55, 0.030);
                foot1 = RendererUtils.HslToRgb(hue1, 0.55, 0.030);
                top0 = RendererUtils.HslToRgb(hue0, 0.5, 0.06 + 0.04 * v0);
                top1 = RendererUtils.HslToRgb(hue1, 0.5, 0.06 + 0.04 * v1);
            }
            else
            {
                foot0 = RendererUtils.HslToRgb(hue0, 0.9, 0.05);
                foot1 = RendererUtils.HslToRgb(hue1, 0.9, 0.05);
                top0 = RendererUtils.HslToRgb(hue0, 0.95, 0.22 + 0.16 * v0);
                top1 = RendererUtils.HslToRgb(hue1, 0.95, 0.22 + 0.16 * v1);
            }

            int x0 = s * w / Slices;
            int x1 = (s + 1) * w / Slices;
            int y0 = baseY - (int)(v0 * height);
            int y1 = baseY - (int)(v1 * height);

            quad[0] = new RendererUtils.PolyVertex(x0, baseY, foot0);
            quad[1] = new RendererUtils.PolyVertex(x1, baseY, foot1);
            quad[2] = new RendererUtils.PolyVertex(x1, y1, top1);
            quad[3] = new RendererUtils.PolyVertex(x0, y0, top0);
            RendererUtils.FillConvexPolyLocked(bmp, quad);
        }
    }

    private void DrawTravelStar(double f, double hue, int w, int groundY, float peakH, float beatBoost)
    {
        f %= 1.0;
        if (f < 0) f += 1.0;
        int s = Math.Min((int)(f * Slices), Slices - 1);
        float v = _envelope[s];
        if (v < 0.02f) return;
        int x = (int)(f * w);        int y = groundY - (int)(v * peakH * beatBoost) - 6;
        var sc = RendererUtils.HslToRgb(hue, 0.9, 0.85);
        RendererUtils.GlowLocked(_bmp!, x, y, 10f, sc.R, sc.G, sc.B, 1.0f);
        RendererUtils.GlowLocked(_bmp!, x, y, 3f, 255, 255, 255, 0.95f);
    }

    private void SpawnSparks(int w, int groundY, float peakH)
    {
        for (int n = 0; n < 8; n++)
        {
            for (int i = 0; i < SparkCount; i++)
            {
                if (_sparks[i].Life > 0) continue;
                int s = _rng.Next(Slices);
                float v = _envelope[s];
                if (v < 0.15f) continue;
                float life = 0.5f + (float)_rng.NextDouble() * 0.7f;
                _sparks[i] = new Spark
                {
                    X = s * w / (float)Slices,
                    Y = groundY - v * peakH,
                    Vx = (float)(_rng.NextDouble() - 0.5) * 36f,
                    Vy = -55f - (float)_rng.NextDouble() * 95f,
                    Life = life,
                    MaxLife = life,
                    Hue = SliceHue(s)
                };
                break;
            }
        }
    }

    private void UpdateSparks(float dt)
    {
        if (_bmp is null) return;
        for (int i = 0; i < SparkCount; i++)
        {
            var sp = _sparks[i];
            if (sp.Life <= 0) continue;

            sp.Life -= dt;
            sp.X += sp.Vx * dt;
            sp.Y += sp.Vy * dt;
            sp.Vy += 42f * dt; // 轻微重力，火星升后回落
            sp.Vx *= 0.99f;
            _sparks[i] = sp;
            if (sp.Life <= 0) continue;

            var c = RendererUtils.HslToRgb(sp.Hue, 0.9, 0.75);
            float fade = sp.Life / sp.MaxLife;
            RendererUtils.GlowLocked(_bmp, (int)sp.X, (int)sp.Y, 2.4f, c.R, c.G, c.B, 0.85f * fade);
        }
    }

    public void Cleanup()
    {
        _bmp = null;
        _slow = null;
    }
}
