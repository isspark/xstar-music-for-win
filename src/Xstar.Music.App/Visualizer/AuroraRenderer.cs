using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Xstar.Music.App.Visualizer;

/// <summary>
/// 极光帷幕：夜幕下多根粉 / 品红 / 紫 / 蓝色光柱自地平线升起，
/// 柱内光丝持续向上流动，帧间衰减拉出纵向光轨；频谱驱动每根帷幕的
/// 高低明暗，鼓点让整片光幕呼吸增亮，顶部是闪烁的星空。
/// 色板以极光粉紫蓝为主，轻微跟随主题强调色。
/// </summary>
public sealed class AuroraRenderer : IVisualizerRenderer
{
    public string StyleId => "aurora";
    public string DisplayName => "极光";
    public bool IsHardwareCompatible => true;

    private WriteableBitmap? _bmp;
    private double _time;
    private Star[]? _stars;
    private float[]? _topEase;
    private readonly Random _rng = new(21);

    // ── 帷幕定义：横向位置、宽度、色相、基础高度、倾斜、对应频段 ──────────
    private readonly Curtain[] _curtains =
    {
        new(0.00f, 0.11f, 318, 0.85f,  0.00f, 0.02f),  // 最左缘品红
        new(0.09f, 0.040f, 222, 0.72f, -0.025f, 0.10f), // 蓝色细束
        new(0.16f, 0.055f, 248, 0.48f,  0.015f, 0.18f),
        new(0.29f, 0.050f, 262, 0.34f, -0.01f, 0.30f),  // 远处暗紫
        new(0.41f, 0.050f, 212, 0.60f,  0.02f, 0.42f),  // 钢青
        new(0.55f, 0.060f, 270, 0.95f, -0.02f, 0.55f),  // 紫色高束
        new(0.64f, 0.075f, 292, 1.02f,  0.015f, 0.66f), // 紫粉
        new(0.73f, 0.085f, 312, 1.03f, -0.01f, 0.78f),  // 粉
        new(0.83f, 0.095f, 320, 1.02f,  0.02f, 0.88f),  // 品红
        new(0.94f, 0.12f,  326, 1.00f, -0.015f, 0.96f), // 大片品红
        new(1.02f, 0.10f,  300, 0.88f,  0.00f, 1.00f),  // 右缘
    };

    private struct Curtain
    {
        public readonly float X, Sigma, Height, Tilt, Bin;
        public readonly double Hue;
        public Curtain(float x, float sigma, double hue, float height, float tilt, float bin)
        { X = x; Sigma = sigma; Hue = hue; Height = height; Tilt = tilt; Bin = bin; }
    }

    private struct Star
    {
        public float X, Y, Phase, Speed;
        public byte Size;
    }

    public void Initialize(WriteableBitmap bitmap)
    {
        _bmp = bitmap;
        _time = 0;
        _topEase = new float[_curtains.Length];
        Array.Fill(_topEase, -1f);

        int n = 150;
        _stars = new Star[n];
        for (int i = 0; i < n; i++)
        {
            // 星点集中在上方夜幕，少量落入中部
            float fy = MathF.Pow((float)_rng.NextDouble(), 1.5f) * 0.58f;
            _stars[i] = new Star
            {
                X = (float)_rng.NextDouble(),
                Y = fy,
                Phase = (float)(_rng.NextDouble() * Math.PI * 2),
                Speed = 0.5f + (float)_rng.NextDouble() * 2.2f,
                Size = (byte)(_rng.NextDouble() < 0.12 ? 2 : 1)
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

            float dt = (float)frame.DeltaTimeMs / 1000f;
            _time += dt;
            double t = _time;

            float bass = frame.BassEnergy, mid = frame.MidEnergy, treble = frame.TrebleEnergy;
            bool beat = frame.BeatDetected;

            // 纵向光轨：光丝向上流动，衰减在身后留下拖影
            RendererUtils.DecayToLocked(_bmp, frame.Background, 0.80f);

            var (accentHue, _, _) = RendererUtils.RgbToHsl(frame.Accent);
            var spec = frame.Spectrum;
            int usable = spec.Length > 0 ? Math.Max(8, (int)(spec.Length * 0.7f)) : 0;

            float breathe = 0.82f + bass * 0.45f + (beat ? 0.18f : 0f);

            // ── 极光帷幕（加法光柱：宽软光晕 + 窄亮光丝）──────────────────
            for (int c = 0; c < _curtains.Length; c++)
            {
                var cu = _curtains[c];

                // 频段能量 → 帷幕起伏；再叠加整体中低频呼吸
                float band = 0f;
                if (usable > 0)
                {
                    int bi = Math.Min((int)(cu.Bin * usable), spec.Length - 1);
                    band = spec[bi];
                }
                float energy = Math.Clamp(
                    (0.16f + band * 0.95f + mid * 0.28f + bass * 0.14f) * breathe, 0f, 1.25f);

                // 缓慢摇摆 + 顶端倾斜
                float sway = (float)Math.Sin(t * (0.25 + c * 0.013) + c * 1.7) * 0.018f;
                float centerX = (cu.X + sway) * w;
                float targetTop = h * Math.Clamp(cu.Height * (1.02f + bass * 0.32f + band * 0.30f), 0.25f, 1.22f);
                // 顶端高度缓动：避免频谱抖动让光柱顶端产生水平阶梯拖影
                float eased = _topEase![c];
                if (eased < 0f) eased = targetTop;
                float k = targetTop > eased ? 1f - MathF.Exp(-dt * 9f) : 1f - MathF.Exp(-dt * 4.5f);
                eased += (targetTop - eased) * k;
                _topEase[c] = eased;
                float topH = eased;
                float sigma = cu.Sigma * w * 0.72f * (0.85f + 0.3f * mid);

                double hue = BlendHue(cu.Hue, accentHue, 0.22);
                var soft = RendererUtils.HslToRgb(hue, 0.85, 0.42);
                var hot = RendererUtils.HslToRgb(hue, 1.0, 0.72);

                DrawCurtain(_bmp, w, h, centerX, topH, sigma, cu.Tilt * w,
                    soft.R, soft.G, soft.B, hot.R, hot.G, hot.B, energy, t, c * 0.9 + cu.Hue * 0.01);
            }

            // ── 地平线微光：根部亮感已由帷幕叠加形成，无需额外辉光 ────────

            // ── 顶部星空 ─────────────────────────────────────────────────
            DrawStars(_bmp, w, h, treble, t);
        }
        finally { RendererUtils.FlushLocked(_bmp); }
    }

    private static unsafe void DrawCurtain(WriteableBitmap bmp, int w, int h,
        float centerX, float topH, float sigma, float tilt,
        byte sr, byte sg, byte sb, byte hr, byte hg, byte hb,
        float energy, double t, double phase)
    {
        int stride = bmp.BackBufferStride;
        byte* basePtr = (byte*)bmp.BackBuffer;

        int lim = Math.Max(4, (int)(sigma * 2.4f));
        int yStart = Math.Max(0, h - (int)topH);

        // 三条纵向光丝：在帷幕内位置固定，亮度沿柱长尺度缓慢向上流动
        float raySigma = sigma * 0.24f;
        float offA = -sigma * 0.55f, offB = sigma * 0.08f, offC = sigma * 0.52f;
        float ph = (float)phase, tt = (float)t;
        float s2inv = 1f / (2f * sigma * sigma);
        float r2inv = 1f / (2f * raySigma * raySigma);

        for (int y = h - 1; y >= yStart; y--)
        {
            float up = (h - 1 - y) / Math.Max(1f, topH); // 0 根部 → 1 顶端
            float xRow = centerX + tilt * up;

            // 根部最亮、向顶端柔化收束至近零（避免平顶）
            float fade = MathF.Pow(1f - up, 1.25f);

            // 柱体长尺度起伏（缓缓上行的光波，不成珠）
            float slow = 0.5f + 0.5f * MathF.Sin(up * 5.5f - tt * 1.4f + ph);
            float body = energy * (fade * 0.98f + 0.02f) * (0.72f + 0.28f * slow);

            // 注：帧间衰减 0.80 下加法输入稳态约放大 5 倍，故输入值需压低
            float wideV = body * 0.14f;

            float ra = MathF.Pow(0.5f + 0.5f * MathF.Sin(up * 4.2f - tt * 1.8f + ph), 1.4f);
            float rb = MathF.Pow(0.5f + 0.5f * MathF.Sin(up * 6.0f - tt * 2.2f + ph * 2.3f + 1.7f), 1.6f);
            float rc = MathF.Pow(0.5f + 0.5f * MathF.Sin(up * 3.4f - tt * 1.3f + ph * 0.5f + 3.3f), 1.5f);
            float vA = body * (0.02f + ra * 0.15f);
            float vB = body * (0.03f + rb * 0.17f);
            float vC = body * (0.02f + rc * 0.13f);
            if (wideV + vB < 0.004f) continue;

            int xi = (int)xRow;
            int iA = (int)(xRow + offA) - xi;
            int iB = (int)(xRow + offB) - xi;
            int iC = (int)(xRow + offC) - xi;
            byte* row = basePtr + y * stride + xi * 4;
            int from = Math.Max(-lim, -xi);
            int to = Math.Min(lim, w - 1 - xi);
            for (int dx = from; dx <= to; dx++)
            {
                float d2 = dx * dx;
                float vs = MathF.Exp(-d2 * s2inv) * wideV;
                float da = dx - iA, db = dx - iB, dc = dx - iC;
                float vh = MathF.Exp(-(da * da) * r2inv) * vA
                         + MathF.Exp(-(db * db) * r2inv) * vB
                         + MathF.Exp(-(dc * dc) * r2inv) * vC;
                if (vs + vh < 0.004f) continue;
                byte* px = row + dx * 4;
                px[0] = (byte)Math.Min(255, px[0] + sb * vs + hb * vh);
                px[1] = (byte)Math.Min(255, px[1] + sg * vs + hg * vh);
                px[2] = (byte)Math.Min(255, px[2] + sr * vs + hr * vh);
            }
        }
    }

    private void DrawStars(WriteableBitmap bmp, int w, int h, float treble, double t)
    {
        if (_stars is null) return;
        foreach (var s in _stars)
        {
            float tw = 0.35f + 0.65f * MathF.Sin(s.Phase + (float)t * s.Speed);
            float v = Math.Clamp(tw * (0.45f + treble * 0.9f), 0.08f, 1f);
            int x = (int)(s.X * w), y = (int)(s.Y * h);
            byte b2 = (byte)(170 * v + 60);
            byte g2 = (byte)(190 * v + 55);
            byte r2 = (byte)(210 * v + 45);
            if (s.Size == 2 && v > 0.55f)
            {
                RendererUtils.GlowLocked(bmp, x, y, 2.4f, r2, g2, b2, 0.7f * v);
                RendererUtils.PutPixelLocked(bmp, x, y, 255, 255, 255);
            }
            else
            {
                RendererUtils.PutPixelLocked(bmp, x, y, r2, g2, b2);
            }
        }
    }

    private static double BlendHue(double a, double b, double t)
    {
        double d = ((b - a + 540) % 360) - 180;
        return (a + d * t + 360) % 360;
    }

    public void Cleanup()
    {
        _bmp = null;
        _stars = null;
    }
}
