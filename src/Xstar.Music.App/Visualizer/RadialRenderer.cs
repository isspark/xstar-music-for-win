using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Xstar.Music.App.Visualizer;

/// <summary>
/// 圆环频谱：彩虹光柱从主环向外辐射，内侧双圈细密刻度，
/// 中心品红辉光，沿环游走的高光星点，beat 时喷发飞散粒子。
/// 帧间衰减制造光迹拖尾。
/// </summary>
public sealed class RadialRenderer : IVisualizerRenderer
{
    public string StyleId => "radial";
    public string DisplayName => "圆形";
    public bool IsHardwareCompatible => true;

    private WriteableBitmap? _bmp;
    private readonly Random _rng = new(9);
    private float[]? _levels;
    private float[]? _bandMax;
    private float[]? _raw;
    private float[]? _bled;
    private double _hueShift;
    private double _starPhase;

    private const int BarCount = 240;
    private const int SparkCount = 64;
    private const double AngleOffset = Math.PI / 2; // 低频置于底部，左右镜像对称
    private Spark[]? _sparks;

    private struct Spark
    {
        public float Angle;
        public float R;
        public float Vr;
        public float Life;
        public float MaxLife;
        public double Hue;
    }

    public void Initialize(WriteableBitmap bitmap)
    {
        _bmp = bitmap;
        _levels = new float[BarCount];
        _bandMax = new float[BarCount];
        _raw = new float[BarCount / 2];
        _bled = new float[BarCount / 2];
        _sparks = new Spark[SparkCount];
        for (int i = 0; i < SparkCount; i++) _sparks[i].Life = 0;
        _hueShift = 0;
        _starPhase = 0;

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
            int minDim = Math.Min(w, h);

            float ringR = minDim * 0.235f;
            float maxOut = minDim * 0.24f;
            float innerLen = minDim * 0.038f;

            // 帧间衰减 → 光柱收缩时留下渐隐光迹（融入主题背景色）
            RendererUtils.DecayToLocked(_bmp, frame.Background, 0.80f);

            var spectrum = frame.Spectrum;
            if (spectrum.Length == 0) return;

            float dt = (float)frame.DeltaTimeMs / 1000f;
            _hueShift += frame.DeltaTimeMs * 0.02;
            _starPhase += dt;

            bool beat = frame.BeatDetected;
            _levels ??= new float[BarCount];
            _bandMax ??= new float[BarCount];

            // 中心保持空心（深色径向暗角，仅用 corner glow 营造氛围）
            var edgeBlue = RendererUtils.HslToRgb(235, 0.8, 0.4);
            RendererUtils.GlowLocked(_bmp, (int)(w * 0.12), (int)(h * 0.15), minDim * 0.28f,
                edgeBlue.R, edgeBlue.G, edgeBlue.B, 0.05f, 2);
            RendererUtils.GlowLocked(_bmp, (int)(w * 0.88), (int)(h * 0.85), minDim * 0.28f,
                edgeBlue.R, edgeBlue.G, edgeBlue.B, 0.05f, 2);

            // ── 频率域采样 → 借光 → 高低音穿插映射 → AGC ────────────────────
            // 频谱沿圆周镜像铺开（第 i 柱与对侧柱同源），且高低频段在圆周上
            // 交替穿插：低频（鼓点）每隔一柱均匀分布到整圈，高频填在中间。
            const int halfBars = BarCount / 2;
            const float UsableFrac = 0.75f;   // 只取前 75% 频段（约 ≤10kHz），更高段能量恒≈0
            int usableBands = Math.Max(8, (int)(spectrum.Length * UsableFrac));

            // 1) 按频率顺序取半环能量（k=0 最低频 → k=halfBars-1 最高频）
            float[] freq = _raw ??= new float[halfBars];
            for (int k = 0; k < halfBars; k++)
            {
                float u = (float)k / halfBars;
                // 角度压缩：高频段映射得更密，中低频铺满更宽频带
                float ft = MathF.Pow(u, 1.25f);
                float fi = ft * usableBands;
                int i0 = Math.Min((int)fi, spectrum.Length - 1);
                int i1 = Math.Min(i0 + 1, spectrum.Length - 1);
                float frac = fi - i0;
                float v = spectrum[i0] * (1 - frac) + spectrum[i1] * frac;

                // 高频预加重（弱补偿，避免把安静频段恒定抬起）
                v = Math.Clamp(v * (1f + u * 0.9f), 0f, 1f);
                freq[k] = v < 0.03f ? 0f : v;
            }

            // 2) 相邻频段“借光”（在频率域做：低频 → 高频单向传播，弱权重）
            float[] bled = _bled ??= new float[halfBars];
            for (int k = 0; k < halfBars; k++)
            {
                float v = freq[k];
                if (k >= 1) v = MathF.Max(v, freq[k - 1] * 0.32f);
                if (k >= 2) v = MathF.Max(v, freq[k - 2] * 0.13f);
                if (k >= 3) v = MathF.Max(v, freq[k - 3] * 0.05f);
                float borrowGain = 0.55f + 0.20f * k / halfBars;
                bled[k] = MathF.Min(1f, MathF.Max(freq[k], v * borrowGain));
            }

            // 3) 高低音穿插：半环位置 t 偶数取低频段、奇数取高频段，
            //    对侧镜像同源，鼓点即可在整圈均匀跳动。
            for (int i = 0; i < BarCount; i++)
            {
                int t = Math.Min(i, BarCount - 1 - i);
                int pair = t / 2;
                int k = (t & 1) == 0 ? pair : halfBars - 1 - pair;
                float v = bled[k];

                // AGC：快攻击、中速释放（≈4 秒峰值减半）
                if (v > _bandMax[i]) _bandMax[i] += (v - _bandMax[i]) * 0.45f;
                else _bandMax[i] *= 0.992f;
                float ceiling = Math.Max(_bandMax[i], 0.16f);
                float norm = Math.Clamp(v / ceiling, 0f, 1f);

                // 较高门限：安静柱回到 0，只有真实起伏才抬起；gamma 接近线性
                norm = norm < 0.09f ? 0f : MathF.Pow(norm, 0.9f);
                norm = Math.Clamp(norm, 0f, 1f);

                _levels[i] += (norm - _levels[i]) * 0.45f;
            }

            float beatBoost = beat ? 1.12f : 1f;

            // ── 内侧两圈细密刻度（先画，处于最暗层）────────────────────────
            for (int i = 0; i < BarCount; i++)
            {
                double angle = (double)i / BarCount * Math.PI * 2 + AngleOffset;
                double cosA = Math.Cos(angle), sinA = Math.Sin(angle);
                double hue = BarHue(i);
                float v = _levels[i];

                var tick1 = RendererUtils.HslToRgb(hue, 0.85, 0.38);
                var tick2 = RendererUtils.HslToRgb(hue, 0.7, 0.26);

                float r1a = ringR * 0.72f, r1b = r1a - (2f + v * 7f);
                RadialSegment(_bmp, cx, cy, r1a, r1b, cosA, sinA, tick1, tick1, 1);

                float r2a = ringR * 0.56f, r2b = r2a - (1.5f + v * 4f);
                RadialSegment(_bmp, cx, cy, r2a, r2b, cosA, sinA, tick2, tick2, 1);
            }

            // ── 主环连续光环（彩虹细点 + 间隔辉光）────────────────────────
            int ringDots = BarCount * 2;
            for (int j = 0; j < ringDots; j++)
            {
                double angle = (double)j / ringDots * Math.PI * 2;
                int x = cx + (int)Math.Round(Math.Cos(angle) * ringR);
                int y = cy + (int)Math.Round(Math.Sin(angle) * ringR);
                double hue = BarHue(j / 2);
                var rc = RendererUtils.HslToRgb(hue, 0.95, 0.72);
                RendererUtils.FillRectLocked(_bmp, x, y, 2, 2, rc.R, rc.G, rc.B);
                if ((j & 3) == 0)
                    RendererUtils.GlowLocked(_bmp, x, y, 3f, rc.R, rc.G, rc.B, 0.45f);
            }

            // ── 彩虹光柱：外射长柱 + 内射短柱 ──────────────────────────────
            for (int i = 0; i < BarCount; i++)
            {
                float v = _levels[i];
                if (v < 0.01f) continue;

                double angle = (double)i / BarCount * Math.PI * 2 + AngleOffset;
                double cosA = Math.Cos(angle), sinA = Math.Sin(angle);
                double hue = BarHue(i);

                var cBase = RendererUtils.HslToRgb(hue, 0.95, 0.60);
                var cTip = RendererUtils.HslToRgb(hue, 1.0, 0.82);
                var cInner = RendererUtils.HslToRgb(hue, 0.85, 0.42);

                float outLen = v * maxOut * beatBoost;
                int tx = cx + (int)Math.Round(cosA * (ringR + outLen));
                int ty = cy + (int)Math.Round(sinA * (ringR + outLen));

                // 外射发光柱（基部 → 尖端，泛白亮）
                RadialSegment(_bmp, cx, cy, ringR + 1, ringR + outLen, cosA, sinA, cBase, cTip, 2);
                // 柱身中段辉光
                float midR = ringR + outLen * 0.55f;
                int mx = cx + (int)Math.Round(cosA * midR);
                int my = cy + (int)Math.Round(sinA * midR);
                RendererUtils.GlowLocked(_bmp, mx, my, 3f, cBase.R, cBase.G, cBase.B, 0.35f);
                // 尖端亮斑
                RendererUtils.GlowLocked(_bmp, tx, ty, 5f, cTip.R, cTip.G, cTip.B, 0.95f);

                // 内射短柱
                RadialSegment(_bmp, cx, cy, ringR - 1, ringR - v * innerLen, cosA, sinA, cBase, cInner, 1);
            }

            // ── 沿环游走的高光星点 ─────────────────────────────────────────
            DrawTravelStar(_bmp, cx, cy, ringR, _starPhase * 0.55, 165); // 青绿
            DrawTravelStar(_bmp, cx, cy, ringR, -_starPhase * 0.4 + 2.1, 315); // 品红

            // ── beat 喷发 + 飞散粒子更新 ───────────────────────────────────
            if (beat) SpawnSparks(ringR);
            UpdateSparks(_bmp, cx, cy, dt);
        }
        finally { RendererUtils.FlushLocked(_bmp); }
    }

    private double BarHue(double index0)
        => ((index0 / BarCount) * 360.0 + _hueShift) % 360;

    private static void DrawTravelStar(WriteableBitmap bmp, int cx, int cy, float ringR, double phase, double hue)
    {
        double a = phase;
        int x = cx + (int)Math.Round(Math.Cos(a) * ringR);
        int y = cy + (int)Math.Round(Math.Sin(a) * ringR);
        var sc = RendererUtils.HslToRgb(hue, 0.9, 0.85);
        RendererUtils.GlowLocked(bmp, x, y, 11f, sc.R, sc.G, sc.B, 1.0f);
        RendererUtils.GlowLocked(bmp, x, y, 3.5f, 255, 255, 255, 0.95f);
    }

    private void SpawnSparks(float ringR)
    {
        if (_sparks is null) return;
        for (int n = 0; n < 10; n++)
        {
            for (int s = 0; s < SparkCount; s++)
            {
                if (_sparks[s].Life <= 0)
                {
                    float a = (float)(_rng.NextDouble() * Math.PI * 2);
                    float life = 0.45f + (float)_rng.NextDouble() * 0.6f;
                    _sparks[s] = new Spark
                    {
                        Angle = a,
                        R = ringR + 2,
                        Vr = 70f + (float)_rng.NextDouble() * 130f,
                        Life = life,
                        MaxLife = life,
                        Hue = _rng.NextDouble() * 360
                    };
                    break;
                }
            }
        }
    }

    private void UpdateSparks(WriteableBitmap bmp, int cx, int cy, float dt)
    {
        if (_sparks is null) return;
        for (int s = 0; s < SparkCount; s++)
        {
            var sp = _sparks[s];
            if (sp.Life <= 0) continue;

            sp.Life -= dt;
            sp.R += sp.Vr * dt;
            sp.Vr *= 0.985f;
            _sparks[s] = sp;
            if (sp.Life <= 0) continue;

            int x = cx + (int)Math.Round(Math.Cos(sp.Angle) * sp.R);
            int y = cy + (int)Math.Round(Math.Sin(sp.Angle) * sp.R);
            var c = RendererUtils.HslToRgb(sp.Hue, 0.9, 0.75);
            float fade = sp.Life / sp.MaxLife;
            RendererUtils.GlowLocked(bmp, x, y, 2.5f, c.R, c.G, c.B, 0.85f * fade);
        }
    }

    /// <summary>
    /// Draws a width-px radial segment by interpolating radius [rA..rB] (either direction)
    /// and colour [ca..cb] along a fixed angle.
    /// </summary>
    private static void RadialSegment(WriteableBitmap bmp, int cx, int cy,
        float rA, float rB, double cosA, double sinA, Color ca, Color cb, int width)
    {
        float dist = MathF.Abs(rB - rA);
        int steps = Math.Max(1, (int)dist);
        int half = width / 2;
        for (int k = 0; k <= steps; k++)
        {
            float t = k / (float)steps;
            float rad = rA + (rB - rA) * t;
            int x = cx + (int)Math.Round(cosA * rad);
            int y = cy + (int)Math.Round(sinA * rad);
            byte r = (byte)(ca.R * (1 - t) + cb.R * t);
            byte g = (byte)(ca.G * (1 - t) + cb.G * t);
            byte bcol = (byte)(ca.B * (1 - t) + cb.B * t);
            RendererUtils.FillRectLocked(bmp, x - half, y - half, width, width, r, g, bcol);
        }
    }

    public void Cleanup()
    {
        _bmp = null;
        _levels = null;
        _bandMax = null;
        _raw = null;
        _bled = null;
        _sparks = null;
    }
}