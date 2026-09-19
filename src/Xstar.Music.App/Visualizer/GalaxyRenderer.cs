using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Xstar.Music.App.Visualizer;

/// <summary>
/// 银河：暗场中一个椭圆星尘球——内部数千颗暗星做差速旋流并闪烁，
/// 外缘是一圈由细密粒子织成、波浪沿环传播的“丝质环”（频谱冲击掀起浪脊），
/// 一颗明亮青色胶囊彗星沿内缘巡走并留下运动光迹，中心一颗微弱星核。
/// 单色青调（色相轻微跟随主题强调色），帧间衰减制造丝绸拖尾。
/// </summary>
public sealed class GalaxyRenderer : IVisualizerRenderer
{
    public string StyleId => "galaxy";
    public string DisplayName => "银河";
    public bool IsHardwareCompatible => true;

    private const int StarCount = 5200;
    private const int RimSpokes = 720;
    private const int EnvSlices = 480;
    private const double YSquash = 0.84; // 星尘球纵向压扁，呈参考图的横向椭圆

    private WriteableBitmap? _bmp;
    private Star[]? _stars;
    private readonly MountainSpectrum _spectrum = new();
    private readonly float[] _envelope = new float[EnvSlices];
    private readonly Random _rng = new(7);
    private double _time;
    private double _hueShift;

    private struct Star
    {
        public float Radius;    // 归一化半径 0..1
        public float Angle;
        public float TwPhase;
        public float TwSpeed;
        public float Spin;      // 差速旋转系数
        public byte Size;
    }

    public void Initialize(WriteableBitmap bitmap)
    {
        _bmp = bitmap;
        _time = 0;
        _hueShift = 0;
        _stars = new Star[StarCount];
        for (int i = 0; i < StarCount; i++)
        {
            // 中心密、外缘疏（幂分布偏向中心）
            float r = MathF.Pow((float)_rng.NextDouble(), 1.55f);
            _stars[i] = new Star
            {
                Radius = r,
                Angle = (float)(_rng.NextDouble() * Math.PI * 2),
                TwPhase = (float)(_rng.NextDouble() * Math.PI * 2),
                TwSpeed = 0.6f + (float)_rng.NextDouble() * 2.6f,
                Spin = 0.4f + (float)_rng.NextDouble() * 0.8f,
                Size = (byte)(_rng.NextDouble() < 0.08 ? 2 : 1)
            };
        }

        bitmap.Lock();
        try { RendererUtils.ClearLocked(bitmap, RendererUtils.DefaultBackground); }
        finally { RendererUtils.FlushLocked(bitmap); }
    }

    public void Render(VisualizerFrame frame)
    {
        if (_bmp is null || _stars is null) return;

        _bmp.Lock();
        try
        {
            int w = _bmp.PixelWidth, h = _bmp.PixelHeight;
            int cx = w / 2, cy = h / 2;
            int minDim = Math.Min(w, h);

            float dt = (float)frame.DeltaTimeMs / 1000f;
            _time += dt;
            _hueShift += frame.DeltaTimeMs * 0.006;
            double t = _time;

            float bass = frame.BassEnergy, mid = frame.MidEnergy, treble = frame.TrebleEnergy;
            bool beat = frame.BeatDetected;

            // 丝质环拖尾：不清屏，向主题底色缓慢衰减
            RendererUtils.DecayToLocked(_bmp, frame.Background, 0.74f);

            var (accentHue, _, _) = RendererUtils.RgbToHsl(frame.Accent);
            double hue = LerpHue(accentHue, 188, 0.55); // 青色为主，轻微跟随主题
            double cometHue = LerpHue(accentHue, 192, 0.72);

            float fieldR = minDim * 0.40f;
            float rimR = minDim * 0.445f;
            float breathe = 1f + bass * 0.035f;

            // ── 内部星尘：差速旋流 + 闪烁（密而静，呈星尘颗粒感）──────────
            float spinBase = 0.045f + bass * 0.10f + (beat ? 0.03f : 0f);
            var starDim = RendererUtils.HslToRgb(hue, 0.6, 0.12);
            var starHot = RendererUtils.HslToRgb(hue, 0.8, 0.42);
            var glint = RendererUtils.HslToRgb(hue, 0.9, 0.8);

            for (int i = 0; i < _stars.Length; i++)
            {
                var s = _stars[i];
                // 内圈略快、外圈近静止
                float angSpeed = spinBase * s.Spin / (0.55f + s.Radius * 1.4f);
                s.Angle += angSpeed * dt;

                float rr = s.Radius * fieldR * breathe;
                float x = cx + MathF.Cos(s.Angle) * rr;
                float y = cy + MathF.Sin(s.Angle) * rr * (float)YSquash;
                int xi = (int)x, yi = (int)y;

                float dens = 1f - 0.5f * s.Radius;                  // 中心略密亮
                float tw = 0.7f + 0.3f * MathF.Sin(s.TwPhase + (float)t * s.TwSpeed);
                float energy = 0.55f + mid * 0.5f + treble * 0.3f;
                float v = Math.Clamp(dens * tw * energy, 0.04f, 1f) * 0.8f;

                byte rC = (byte)(starDim.R + (starHot.R - starDim.R) * v);
                byte gC = (byte)(starDim.G + (starHot.G - starDim.G) * v);
                byte bC = (byte)(starDim.B + (starHot.B - starDim.B) * v);

                if (s.Size == 2)
                {
                    RendererUtils.FillRectLocked(_bmp, xi - 1, yi, 1, 1, rC, gC, bC);
                    RendererUtils.FillRectLocked(_bmp, xi, yi - 1, 1, 1, rC, gC, bC);
                    RendererUtils.FillRectLocked(_bmp, xi, yi, 1, 1, rC, gC, bC);
                    RendererUtils.FillRectLocked(_bmp, xi + 1, yi, 1, 1, rC, gC, bC);
                    RendererUtils.FillRectLocked(_bmp, xi, yi + 1, 1, 1, rC, gC, bC);
                }
                else
                {
                    RendererUtils.FillRectLocked(_bmp, xi, yi, 1, 1, rC, gC, bC);
                }

                // 强闪烁星点：偶发冷光
                if (tw > 0.965f)
                    RendererUtils.GlowLocked(_bmp, xi, yi, 2f, glint.R, glint.G, glint.B, 0.4f * v);

                _stars[i] = s;
            }

            // ── 中心微弱星核 ──────────────────────────────────────────────
            var core = RendererUtils.HslToRgb(hue, 0.9, 0.7);
            RendererUtils.GlowLocked(_bmp, cx, cy, 9f, core.R, core.G, core.B, 0.35f);
            RendererUtils.GlowLocked(_bmp, cx, cy, 2.5f, 220, 250, 255, 0.5f);

            // ── 外缘丝质环：频谱包络 + 多层行波 ───────────────────────────
            var mt = _spectrum.Update(frame.Spectrum);
            MountainSpectrum.BuildEnvelope(mt, _envelope);

            double rot = t * 0.05;
            float waveEnergy = 0.6f + 0.5f * Math.Clamp(mid + bass, 0f, 1.2f);
            float bandBase = minDim * 0.045f;   // 丝质环基础宽度
            float waveAmp = minDim * 0.030f;    // 行波振幅
            float beatLift = minDim * 0.05f;    // 频谱浪脊高度

            var lipDim = RendererUtils.HslToRgb(hue, 0.85, 0.18);
            var lipHot = RendererUtils.HslToRgb(hue, 0.95, 0.62);
            var lipCrest = RendererUtils.HslToRgb(hue, 1.0, 0.82);

            for (int i = 0; i < RimSpokes; i++)
            {
                double a = (double)i / RimSpokes * Math.PI * 2 + rot;
                float u = (float)i / RimSpokes;
                int ei = Math.Min((int)(u * EnvSlices), EnvSlices - 1);
                float level = _envelope[ei];

                // 三列不同频率/速度的行波叠加（驻波→丝绸感）
                double wv = Math.Sin(a * 3.0 + t * 1.7) * 0.45
                          + Math.Sin(a * 6.5 - t * 2.3 + 2.1) * 0.32
                          + Math.Sin(a * 12.0 + t * 3.1) * 0.23;
                float crest = (float)(wv * 0.5 + 0.5) * waveEnergy;
                // 每根辐条独立的颗粒抖动 → 织物/丝网纹理
                float grain = 0.65f + 0.5f * Hash01(i * 3 + 17);

                float outer = rimR + crest * waveAmp * 3f + level * beatLift * (0.7f + bass * 0.6f);
                float inner = rimR - bandBase - level * minDim * 0.012f;
                float cosA = MathF.Cos((float)a), sinA = MathF.Sin((float)a);

                // 沿半径铺细密粒子，亮度靠外唇达到峰值
                int steps = (int)((outer - inner) / 2f);
                for (int k = 0; k <= steps; k++)
                {
                    float fr = k / (float)Math.Max(1, steps); // 0 内缘 → 1 外缘
                    float rad = inner + (outer - inner) * fr;
                    // 细丝：亮度包络在靠外 40% 处最亮
                    float profile = MathF.Pow(fr, 1.5f) * (0.30f + 0.7f * crest)
                                  * (0.5f + level * 0.7f) * grain;
                    if (profile < 0.05f) continue;
                    int sx = cx + (int)(cosA * rad);
                    int sy = cy + (int)(sinA * rad * (float)YSquash);
                    byte rr2 = (byte)(lipDim.R + (lipHot.R - lipDim.R) * Math.Min(1f, profile));
                    byte gg2 = (byte)(lipDim.G + (lipHot.G - lipDim.G) * Math.Min(1f, profile));
                    byte bb2 = (byte)(lipDim.B + (lipHot.B - lipDim.B) * Math.Min(1f, profile));
                    RendererUtils.FillRectLocked(_bmp, sx, sy, 1, 1, rr2, gg2, bb2);
                }

                // 浪脊上的冷光（隔点采样）
                if ((i & 2) == 0 && (level > 0.16f || crest > 0.82f))
                {
                    int ox = cx + (int)(cosA * outer);
                    int oy = cy + (int)(sinA * outer * (float)YSquash);
                    float str = Math.Clamp(0.22f + level * 0.55f + MathF.Max(0f, crest - 0.8f) * 1.5f, 0f, 0.9f);
                    RendererUtils.GlowLocked(_bmp, ox, oy, 2.6f, lipCrest.R, lipCrest.G, lipCrest.B, str);
                }
            }

            // ── 胶囊彗星：沿内缘巡走，低频脉冲拉长，带光迹 ─────────────────
            double ca = t * 0.55 + Math.Sin(t * 0.27) * 0.6;
            float orbit = fieldR * (0.62f + 0.06f * MathF.Sin((float)(t * 0.8)));
            float cometX = cx + MathF.Cos((float)ca) * orbit;
            float cometY = cy + MathF.Sin((float)ca) * orbit * (float)YSquash;
            float len = 20f + bass * 52f + (beat ? 16f : 0f);
            float halfW = 6.5f;
            // 胶囊沿径向摆放
            float dirX = MathF.Cos((float)ca), dirY = MathF.Sin((float)ca) * (float)YSquash;
            float dl = MathF.Sqrt(dirX * dirX + dirY * dirY);
            dirX /= dl; dirY /= dl;

            var cometBody = RendererUtils.HslToRgb(cometHue, 0.95, 0.62);
            var cometCore = RendererUtils.HslToRgb(cometHue, 0.7, 0.92);
            RendererUtils.GlowLocked(_bmp, (int)cometX, (int)cometY, len * 0.85f,
                cometBody.R, cometBody.G, cometBody.B, 0.32f, 2);
            int stamps = Math.Max(6, (int)(len / 4f));
            for (int k = 0; k <= stamps; k++)
            {
                float f = k / (float)stamps - 0.5f;               // -0.5..0.5
                float round = MathF.Sqrt(MathF.Max(0f, 1f - 4f * f * f)); // 圆角端
                float rad = halfW * (0.55f + 0.45f * round);
                int px = (int)(cometX + dirX * f * len);
                int py = (int)(cometY + dirY * f * len);
                RendererUtils.GlowLocked(_bmp, px, py, rad * 1.5f, cometBody.R, cometBody.G, cometBody.B, 0.8f);
            }
            // 中心亮芯（加法叠白，避免覆盖式画线在辉光中留出暗缝）
            int corePts = 7;
            for (int k = 0; k < corePts; k++)
            {
                float f = -0.30f + 0.60f * k / (corePts - 1);
                int px = (int)(cometX + dirX * f * len);
                int py = (int)(cometY + dirY * f * len);
                RendererUtils.GlowLocked(_bmp, px, py, 2.6f,
                    cometCore.R, cometCore.G, cometCore.B, 0.85f);
            }
        }
        finally { RendererUtils.FlushLocked(_bmp); }
    }

    private static double LerpHue(double a, double b, double t)
    {
        double d = ((b - a + 540) % 360) - 180;
        return (a + d * t + 360) % 360;
    }

    /// <summary>Deterministic pseudo-random in [0,1) for a given integer seed.</summary>
    private static float Hash01(int seed)
    {
        float x = MathF.Sin(seed * 12.9898f) * 43758.5453f;
        return x - MathF.Floor(x);
    }

    public void Cleanup()
    {
        _bmp = null;
        _stars = null;
    }
}
