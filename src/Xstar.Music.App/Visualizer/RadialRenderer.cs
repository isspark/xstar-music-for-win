using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Xstar.Music.App.Visualizer;

/// <summary>
/// 彩虹针柱环：基环上是一根根独立的径向圆角细柱，柱长随频谱能量伸缩，
/// 安静处收缩成圆点刻度；色相沿圆周固定铺开（顶部红 → 顺时针
/// 橙黄绿青蓝紫品红），不随时间转动。底部左右两团氛围辉光
/// （左紫蓝 / 右红橙黄）。柱体位置固定，帧间衰减只留下短促光迹。
/// </summary>
public sealed class RadialRenderer : IVisualizerRenderer
{
    public string StyleId => "radial";
    public string DisplayName => "圆形";
    public bool IsHardwareCompatible => true;

    private WriteableBitmap? _bmp;
    private float[]? _raw;
    private float[]? _levels;
    private float _globalPeak;   // 全局共享 AGC 峰值：柱长随真实能量错落
    private float _bassEma;
    private float _bassSlow;
    private float _pulse;      // 节奏脉冲包络：起音冲高、随后回落

    private const int BarCount = 176;
    private const double AngleStart = -Math.PI / 2; // 第一柱（最低频）在正上方，顺时针铺开

    // 沿环色相分布的关键点（方位角 → 色相），拟合参考图：
    // 顶红、右中青、底部品红、左中绿。两段之间线性插值。
    private static readonly double[] HueAz = { 0, 45, 90, 135, 180, 225, 270, 315, 360 };
    private static readonly double[] HueVal = { 0, 45, 175, 250, 305, 385, 480, 530, 720 };

    public void Initialize(WriteableBitmap bitmap)
    {
        _bmp = bitmap;
        _raw = new float[BarCount];
        _levels = new float[BarCount];
        _globalPeak = 0;
        _bassEma = _bassSlow = _pulse = 0;

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

            // 帧间衰减 → 柱收缩时残留极短光迹（融入主题背景色）
            RendererUtils.DecayToLocked(_bmp, frame.Background, 0.60f);

            var spectrum = frame.Spectrum;
            if (spectrum.Length == 0) return;

            float dt = (float)frame.DeltaTimeMs / 1000f;
            bool beat = frame.BeatDetected;

            // 自检测节奏脉冲：低频能量高于其缓慢平均 → 起音，整圈随节拍轻跳。
            _bassEma += (frame.BassEnergy - _bassEma) * (1f - MathF.Exp(-dt * 9f));
            _bassSlow += (frame.BassEnergy - _bassSlow) * (1f - MathF.Exp(-dt * 0.8f));
            float ratio = _bassSlow > 1e-4f ? _bassEma / _bassSlow : 1f;
            float attack = Math.Clamp((ratio - 1.10f) * 2.2f, 0f, 1f);
            attack = MathF.Max(attack, Math.Clamp((_bassEma - _bassSlow) * 3f, 0f, 1f));
            _pulse = MathF.Max(_pulse - dt * 3f, attack);
            if (beat) _pulse = 1f;

            float baseR = minDim * 0.275f * (1f + _pulse * 0.04f);
            float maxLen = minDim * 0.155f;

            // ── 底部氛围辉光：左紫蓝 / 右红橙黄（参考图），随低频轻微涨落 ──
            float amb = 0.85f + _bassEma * 0.9f + _pulse * 0.25f;
            RendererUtils.GlowLocked(_bmp, (int)(w * 0.05), (int)(h * 1.00), minDim * 0.40f,
                190, 50, 255, 0.18f * amb, 3);
            RendererUtils.GlowLocked(_bmp, (int)(w * 0.24), (int)(h * 1.04), minDim * 0.37f,
                50, 80, 255, 0.13f * amb, 3);
            RendererUtils.GlowLocked(_bmp, (int)(w * 0.92), (int)(h * 0.95), minDim * 0.40f,
                255, 50, 70, 0.16f * amb, 3);
            RendererUtils.GlowLocked(_bmp, (int)(w * 1.05), (int)(h * 1.01), minDim * 0.38f,
                255, 165, 30, 0.22f * amb, 3);

            // ── 频率域采样 → 全局 AGC → 帧间平滑 ─────────────────────────
            // 频谱沿环顺序铺开：最低频在顶部红区，顺时针频率升高。
            const float UsableFrac = 0.75f;   // 只取前 75% 频段（约 ≤10kHz）
            int usableBands = Math.Max(8, (int)(spectrum.Length * UsableFrac));

            _raw ??= new float[BarCount];
            _levels ??= new float[BarCount];

            // 1) 重采样 + 高频预加重，同时统计本帧全局峰值
            float global = 0f;
            for (int i = 0; i < BarCount; i++)
            {
                float u = (float)i / BarCount;
                // 角度压缩：高频段映射得更密，中低频铺满更宽频带
                float fi = MathF.Pow(u, 1.35f) * usableBands;
                int i0 = Math.Min((int)fi, spectrum.Length - 1);
                int i1 = Math.Min(i0 + 1, spectrum.Length - 1);
                float frac = fi - i0;
                float v = spectrum[i0] * (1 - frac) + spectrum[i1] * frac;

                // 高频预加重（弱补偿，安静频段不抬起）
                v = Math.Clamp(v * (1f + u * 0.8f), 0f, 1f);
                v = v < 0.03f ? 0f : v;
                _raw[i] = v;
                if (v > global) global = v;
            }

            // 2) 全局 AGC：所有柱共用同一个缓慢峰值，安静段落就只剩点刻度，
            //    柱长差异真实保留（参考图错落的针柱，而不是等高栅栏）。
            if (global > _globalPeak) _globalPeak += (global - _globalPeak) * 0.5f;
            else _globalPeak *= 0.996f;
            float peakCeiling = Math.Max(_globalPeak, 0.38f);
            for (int i = 0; i < BarCount; i++)
            {
                float norm = Math.Clamp(_raw[i] / peakCeiling, 0f, 1f);
                // 门限 + gamma：弱柱收成刻度点，强冲击才拉成长针
                norm = norm < 0.10f ? 0f : MathF.Pow(norm, 1.55f);
                _levels[i] += (norm - _levels[i]) * 0.55f;
            }

            // ── 径向圆角针柱 ──────────────────────────────────────────────
            float beatGain = 1f + _pulse * 0.45f;
            for (int i = 0; i < BarCount; i++)
            {
                float v = Math.Clamp(_levels[i] * beatGain, 0f, 1f);
                double a = AngleStart + (double)i / BarCount * Math.PI * 2;
                float ux = (float)Math.Cos(a);
                float uy = (float)Math.Sin(a);

                // 零能量也保留 2px 圆头刻度，形成参考图里的“点环”
                float len = 2f + v * maxLen;
                double hue = RingHue((double)i / BarCount);
                var col = RendererUtils.HslToRgb(hue, 0.95, 0.57 + 0.20 * v);

                DrawCapsuleLocked(_bmp, cx, cy, ux, uy, baseR, baseR + len,
                    col.R, col.G, col.B);
            }
        }
        finally { RendererUtils.FlushLocked(_bmp); }
    }

    /// <summary>方位比例 u（0=正上方，顺时针）→ 沿环固定彩虹色相。</summary>
    private static double RingHue(double u)
    {
        double az = u * 360.0;
        for (int k = 0; k < HueAz.Length - 1; k++)
        {
            if (az <= HueAz[k + 1])
            {
                double t = (az - HueAz[k]) / (HueAz[k + 1] - HueAz[k]);
                return (HueVal[k] + (HueVal[k + 1] - HueVal[k]) * t) % 360.0;
            }
        }
        return 0;
    }

    /// <summary>
    /// 沿径向画约 3px 宽的圆角（胶囊）细柱：中心线每 1px 落一个 3×3 戳记，
    /// 戳记内像素按“到柱中心线的垂直距离”做 alpha 软边——柱身抗锯齿，
    /// 首尾戳记天然形成圆头。颜色与背景做覆盖混合以保持针柱鲜明。
    /// </summary>
    private static unsafe void DrawCapsuleLocked(WriteableBitmap bmp,
        float cx, float cy, float ux, float uy, float r0, float r1,
        byte cr, byte cg, byte cb)
    {
        int w = bmp.PixelWidth, h = bmp.PixelHeight, stride = bmp.BackBufferStride;
        byte* basePtr = (byte*)bmp.BackBuffer;

        // 柱身法线（切向单位向量）
        float nx = -uy, ny = ux;

        int steps = (int)MathF.Ceiling(r1 - r0);
        for (int t = 0; t <= steps; t++)
        {
            float rr = r0 + t;
            int px = (int)MathF.Round(cx + ux * rr);
            int py = (int)MathF.Round(cy + uy * rr);

            for (int dy = -1; dy <= 1; dy++)
            {
                int y = py + dy;
                if (y < 0 || y >= h) continue;
                byte* row = basePtr + y * stride;
                for (int dx = -1; dx <= 1; dx++)
                {
                    int x = px + dx;
                    if (x < 0 || x >= w) continue;

                    // 像素中心到柱中心线的垂直距离
                    float dist = MathF.Abs(dx * nx + dy * ny);
                    // 柱芯实、1px 软边半透；超出戳记宽度的角点丢弃
                    float a = dist <= 0.55f ? 1f : dist <= 1.45f ? 0.5f : 0f;
                    if (a <= 0f) continue;

                    byte* ptr = row + x * 4;
                    float ia = 1f - a;
                    ptr[0] = (byte)(ptr[0] * ia + cb * a);
                    ptr[1] = (byte)(ptr[1] * ia + cg * a);
                    ptr[2] = (byte)(ptr[2] * ia + cr * a);
                    ptr[3] = 255;
                }
            }
        }
    }

    public void Cleanup()
    {
        _bmp = null;
        _raw = null;
        _levels = null;
    }
}
