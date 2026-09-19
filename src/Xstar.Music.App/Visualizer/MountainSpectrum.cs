using System.Windows.Media;

namespace Xstar.Music.App.Visualizer;

/// <summary>
/// 频谱 → 山脉高度的共享信号链：频段重采样、高频预加重、低→高单向“借光”、
/// 高低频镜像穿插、逐带 AGC（快攻击慢释放）与时间平滑，最后合并左右镜像柱。
/// 圆形（RadialRenderer）与平铺（WaveRenderer）山脉共用同一套起伏语言。
/// </summary>
internal sealed class MountainSpectrum
{
    public const int BarCount = 240;
    public const int Half = BarCount / 2;
    private const float UsableFrac = 0.75f; // 只取前 75% 频段（约 ≤10kHz）

    private readonly float[] _levels = new float[BarCount];
    private readonly float[] _bandMax = new float[BarCount];
    private readonly float[] _freq = new float[Half];
    private readonly float[] _bled = new float[Half];
    private readonly float[] _merged = new float[Half];

    /// <summary>
    /// 输入当前帧频谱，输出长度 <see cref="Half"/> 的山脉高度（k=0 最低频）。
    /// </summary>
    public float[] Update(ReadOnlySpan<float> spectrum)
    {
        if (spectrum.Length == 0)
        {
            Array.Clear(_merged);
            return _merged;
        }

        // 1) 按频率顺序取能量（角度压缩 + 高频预加重 + 静噪门限）
        int usableBands = Math.Max(8, (int)(spectrum.Length * UsableFrac));
        for (int k = 0; k < Half; k++)
        {
            float u = (float)k / Half;
            float fi = MathF.Pow(u, 1.25f) * usableBands;
            int i0 = Math.Min((int)fi, spectrum.Length - 1);
            int i1 = Math.Min(i0 + 1, spectrum.Length - 1);
            float frac = fi - i0;
            float v = spectrum[i0] * (1 - frac) + spectrum[i1] * frac;
            v = Math.Clamp(v * (1f + u * 0.9f), 0f, 1f);
            _freq[k] = v < 0.03f ? 0f : v;
        }

        // 2) 相邻频段“借光”（低频 → 高频单向传播，弱权重）
        for (int k = 0; k < Half; k++)
        {
            float v = _freq[k];
            if (k >= 1) v = MathF.Max(v, _freq[k - 1] * 0.32f);
            if (k >= 2) v = MathF.Max(v, _freq[k - 2] * 0.13f);
            if (k >= 3) v = MathF.Max(v, _freq[k - 3] * 0.05f);
            float borrowGain = 0.55f + 0.20f * k / Half;
            _bled[k] = MathF.Min(1f, MathF.Max(_freq[k], v * borrowGain));
        }

        // 3) 高低音镜像穿插 + AGC + gamma + 时间平滑
        for (int i = 0; i < BarCount; i++)
        {
            int t = Math.Min(i, BarCount - 1 - i);
            int pair = t / 2;
            int k = (t & 1) == 0 ? pair : Half - 1 - pair;
            float v = _bled[k];

            if (v > _bandMax[i]) _bandMax[i] += (v - _bandMax[i]) * 0.45f;
            else _bandMax[i] *= 0.992f;
            float ceiling = Math.Max(_bandMax[i], 0.16f);
            float norm = Math.Clamp(v / ceiling, 0f, 1f);
            norm = norm < 0.09f ? 0f : MathF.Pow(norm, 0.9f);

            _levels[i] += (norm - _levels[i]) * 0.45f;
        }

        // 4) 同一频率的左右镜像柱合并
        for (int k = 0; k < Half; k++)
            _merged[k] = (_levels[2 * k] + _levels[BarCount - 1 - 2 * k]) * 0.5f;

        return _merged;
    }

    /// <summary>
    /// 把半环山脉高度沿完整行程铺开并做空间平滑 + gamma 压缩，让缓坡与尖峰层次分明。
    /// <paramref name="lowAtEnds"/>=true：首尾最低频、正中最高频（圆形剪开铺平的顺序）；
    /// =false：正中最低频、向两侧边缘频率升高（平铺波形的构图：中央主山脉，
    /// 高频锯齿峰坡向两边，屏幕两端自然落入山谷）。
    /// </summary>
    public static void BuildEnvelope(ReadOnlySpan<float> mountain, Span<float> dst, bool lowAtEnds = true)
    {
        int sCount = dst.Length;
        int bands = mountain.Length;
        Span<float> raw = sCount <= 512 ? stackalloc float[sCount] : new float[sCount];

        for (int s = 0; s < sCount; s++)
        {
            float u = (float)s / sCount;
            float m = u <= 0.5f ? u * 2f : 2f - u * 2f;
            if (!lowAtEnds) m = 1f - m;
            float fi = MathF.Pow(m, 0.92f) * (bands - 1);
            int k0 = Math.Min((int)fi, bands - 1);
            int k1 = Math.Min(k0 + 1, bands - 1);
            float f = fi - k0;
            raw[s] = mountain[k0] * (1 - f) + mountain[k1] * f;
        }

        for (int s = 0; s < sCount; s++)
        {
            float a = raw[(s - 1 + sCount) % sCount];
            float b = raw[s];
            float c = raw[(s + 1) % sCount];
            float sm = a * 0.22f + b * 0.56f + c * 0.22f;
            dst[s] = MathF.Pow(sm, 1.6f);
        }
    }

    /// <summary>
    /// 山脊颜色：随高度变亮；显著高于两侧山坡的峰顶泛白（雪顶）。
    /// </summary>
    public static Color RidgeColor(double hue, float v, float prev, float next)
    {
        var ridge = RendererUtils.HslToRgb(hue, 1.0, 0.60 + 0.26 * v);
        float sharpness = Math.Clamp(v - 0.5f * (prev + next), 0f, 1f);
        float cap = Math.Clamp(sharpness * 4f, 0f, 0.8f) * v;
        if (cap <= 0.01f) return ridge;
        return Color.FromRgb(
            (byte)(ridge.R + (255 - ridge.R) * cap),
            (byte)(ridge.G + (255 - ridge.G) * cap),
            (byte)(ridge.B + (255 - ridge.B) * cap));
    }
}
