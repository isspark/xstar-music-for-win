using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Xstar.Music.App.Visualizer;

/// <summary>
/// 光轨（平面版）：屏幕中央一条从左向右贯通的彩色线条。
/// 静默 / 未播放时保持水平；播放时由平滑后的低 / 中 / 高频能量驱动的多组向 +x 行波叠加，
/// 让线条沿长度持续起伏并向右延申。随音乐节奏，线条会分裂成多条子线再重新合并
/// （合并即只保留中间一条）；分裂数量、各线间距与光晕强弱都由节奏能量决定。
/// 帧间轻微衰减形成柔和拖尾，并以强调色沿 x 做色相渐变。
/// </summary>
public sealed class RibbonRenderer : IVisualizerRenderer
{
    public string StyleId => "ribbon";
    public string DisplayName => "光轨";
    public bool IsHardwareCompatible => true;

    private const float Tau = MathF.PI * 2f;

    private WriteableBitmap? _bmp;
    private float _bassEma, _midEma, _trebleEma, _beatEnv;
    private readonly float[] _prevSpec = new float[64];
    private float _fluxEma;     // 频谱通量的缓慢平均，用作自适应起音阈值
    private float _split;       // 分裂包络：随节奏张开，随后回落合并
    private double _time;
    private Color _accent = Color.FromRgb(110, 139, 255);
    private Color _background = RendererUtils.DefaultBackground;

    public void Initialize(WriteableBitmap bitmap)
    {
        _bmp = bitmap;
        _time = 0;
        _bassEma = _midEma = _trebleEma = _beatEnv = _fluxEma = _split = 0;
        Array.Clear(_prevSpec);
        _accent = Color.FromRgb(110, 139, 255);
        _background = RendererUtils.DefaultBackground;

        bitmap.Lock();
        try { DrawIdle(bitmap); }
        finally { RendererUtils.FlushLocked(bitmap); }
    }

    public void Render(VisualizerFrame frame)
    {
        if (_bmp is null) return;

        _bmp.Lock();
        try
        {
            int w = _bmp.PixelWidth, h = _bmp.PixelHeight;
            _accent = frame.Accent;
            _background = frame.Background;

            float dt = (float)Math.Clamp(frame.DeltaTimeMs / 1000.0, 0.0, 0.08);
            _time += dt;

            // 乐响平滑：避免逐帧起伏让线条闪烁抖动
            float la = 1f - MathF.Exp(-dt * 6f);
            _bassEma += (frame.BassEnergy - _bassEma) * la;
            _midEma += (frame.MidEnergy - _midEma) * la;
            _trebleEma += (frame.TrebleEnergy - _trebleEma) * la;
            _beatEnv = frame.BeatDetected ? 1f : MathF.Max(0f, _beatEnv - dt * 3.2f);

            // 静止帧（未播放 / 无音频数据）：清屏后只留一条水平线
            if (frame.Spectrum.Length == 0 && frame.Waveform.Length == 0)
            {
                DrawIdle(_bmp);
                return;
            }

            float energy = Math.Clamp(0.42f * _bassEma + 0.40f * _midEma + 0.30f * _trebleEma, 0f, 1.4f);
            float amplify = h * 0.11f;
            float bright = Math.Clamp(0.45f + energy * 1.05f + _beatEnv * 0.45f, 0f, 1.5f);
            // 光晕强度随节奏起伏（低频铺底、鼓点冲高）
            float glow = Math.Clamp(energy * 1.05f + _beatEnv * 1.05f, 0f, 1.6f);

            // 起音检测：频谱通量（各频带正向增量之和）对其缓慢平均的比值。
            // 与绝对响度无关，故安静 / 响亮段落都能在每次鼓点 / 音符出现时触发。
            var spec = frame.Spectrum;
            float flux = 0f;
            int n = Math.Min(spec.Length, _prevSpec.Length);
            if (n > 0)
            {
                for (int i = 0; i < n; i++)
                {
                    float d = spec[i] - _prevSpec[i];
                    if (d > 0f) flux += d;
                }
                flux /= n;
                Array.Copy(spec, _prevSpec, n);
            }
            _fluxEma += (flux - _fluxEma) * (1f - MathF.Exp(-dt * 0.35f));
            float ratio = _fluxEma > 1e-5f ? flux / _fluxEma : 0f;
            float onset = Math.Clamp((ratio - 1.08f) * 2.0f, 0f, 1f);

            // 分裂包络：起音 + 鼓点驱动，响亮段少量保底；节拍之间回落到 0（只剩一条）
            float splitTarget = Math.Clamp(onset + _beatEnv * 0.5f + MathF.Max(0f, energy - 0.6f) * 0.4f, 0f, 1f);
            _split += (splitTarget - _split) * (1f - MathF.Exp(-dt * (splitTarget > _split ? 16f : 4.5f)));

            // 轻微拖尾：留存清晰线条而非糊成一片
            RendererUtils.DecayToLocked(_bmp, frame.Background, 0.55f);

            float cy = h * 0.5f;
            var (accentHue, _, _) = RendererUtils.RgbToHsl(frame.Accent);
            float t = (float)_time;

            // 分裂数量与线条间距都由节奏决定：reach 越大，越外侧的子线才出现；
            // _split=0 时只有 c=0 一条（即“合并”）。
            const int maxSide = 3;
            float laneGap = h * 0.048f * (0.45f + energy * 0.8f);
            float reach = _split * (maxSide + 0.5f);

            for (int c = -maxSide; c <= maxSide; c++)
            {
                float active = Math.Clamp(reach - Math.Abs(c), 0f, 1f);
                if (c != 0 && active <= 0.01f) continue;

                float offset = c * laneGap * (0.2f + 0.8f * active);
                float intensity = c == 0 ? bright * 0.82f : bright * 0.72f * (0.35f + 0.65f * active);
                float laneGlow = c == 0 ? glow : glow * (0.35f + 0.65f * active);
                DrawTrace(_bmp, w, cy, amplify, t, accentHue, c * 0.7f, c * 13.0, offset, intensity, laneGlow);
            }
        }
        finally { RendererUtils.FlushLocked(_bmp); }
    }

    /// <summary>绘制一条沿 x 贯通的起伏线；<paramref name="phaseShift"/> 让各子线波形略有差异，
    /// <paramref name="yBias"/> 用于把分裂线上下移开，<paramref name="hueShift"/> 微调色相，
    /// <paramref name="glow"/> 控制随节奏变化的光晕强弱。</summary>
    private void DrawTrace(WriteableBitmap bmp, int w, float cy, float amplify, float t,
        double accentHue, float phaseShift, double hueShift, float yBias, float intensity, float glow)
    {
        if (intensity <= 0.01f) return;
        if (glow > 0.01f)
            TracePass(bmp, w, cy, amplify, t, accentHue, phaseShift, hueShift, yBias, intensity * glow * 0.32f, true);
        TracePass(bmp, w, cy, amplify, t, accentHue, phaseShift, hueShift, yBias, intensity, false);
    }

    private void TracePass(WriteableBitmap bmp, int w, float cy, float amplify, float t,
        double accentHue, float phaseShift, double hueShift, float yBias, float intensity, bool glowPass)
    {
        bool has = false;
        int prevX = 0, prevY = 0;
        Color prevColor = default;

        for (int x = 0; x < w; x++)
        {
            float u = x / (float)(w - 1);

            // 多组向 +x 行进的正弦叠加，各频段能量决定对应波的振幅
            float y = 0f;
            y += MathF.Sin(Tau * 1.0f * u - t * 0.55f + phaseShift) * _bassEma;
            y += MathF.Sin(Tau * 1.9f * u - t * 0.80f + phaseShift) * (_bassEma * 0.8f + _midEma * 0.3f);
            y += MathF.Sin(Tau * 3.1f * u - t * 1.10f + phaseShift) * _midEma;
            y += MathF.Sin(Tau * 4.7f * u - t * 1.45f + phaseShift) * (_midEma * 0.6f + _trebleEma * 0.4f);
            y += MathF.Sin(Tau * 6.9f * u - t * 1.85f + phaseShift) * (_trebleEma * 0.8f);
            y += MathF.Sin(Tau * 9.3f * u - t * 2.35f + phaseShift) * (_trebleEma * 0.5f);
            y *= 0.5f;

            int py = (int)MathF.Round(cy - y * amplify + yBias);

            double hue = accentHue + u * 150.0 + _time * 18.0 + hueShift;
            var color = RendererUtils.HslToRgb(hue, 0.95, 0.60);

            if (has)
            {
                if (glowPass)
                    RendererUtils.DrawGlowLineAddLocked(bmp, prevX, prevY, prevColor, x, py, color, intensity, 3);
                else
                    RendererUtils.DrawSoftLineAddLocked(bmp, prevX, prevY, prevColor, x, py, color, intensity);
            }

            has = true; prevX = x; prevY = py; prevColor = color;
        }
    }

    /// <summary>未播放 / 静默时的静态形态：一条水平彩色线。</summary>
    private void DrawIdle(WriteableBitmap bmp)
    {
        int w = bmp.PixelWidth, h = bmp.PixelHeight;
        RendererUtils.ClearLocked(bmp, _background);

        int cy = h / 2;
        var (accentHue, _, _) = RendererUtils.RgbToHsl(_accent);
        Color prev = default;
        for (int x = 0; x < w; x++)
        {
            double hue = accentHue + x / (double)Math.Max(1, w - 1) * 150.0;
            var color = RendererUtils.HslToRgb(hue, 0.9, 0.58);
            if (x > 0) RendererUtils.DrawSoftLineAddLocked(bmp, x - 1, cy, prev, x, cy, color, 0.5f);
            prev = color;
        }
    }

    public void Cleanup()
    {
        _bmp = null;
    }
}
