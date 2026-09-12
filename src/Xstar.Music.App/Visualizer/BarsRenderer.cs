using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Xstar.Music.App.Visualizer;

public sealed class BarsRenderer : IVisualizerRenderer
{
    public string StyleId => "bars";
    public string DisplayName => "柱状";
    public bool IsHardwareCompatible => true;

    private WriteableBitmap? _bmp;
    private float[]? _peaks;

    public void Initialize(WriteableBitmap bitmap) { _bmp = bitmap; _peaks = null; }

    public void Render(VisualizerFrame frame)
    {
        if (_bmp is null) return;
        _bmp.Lock();
        try
        {
            int w = _bmp.PixelWidth, h = _bmp.PixelHeight;

            // Theme-tinted deep-space backdrop.
            var bg = frame.Background;
            RendererUtils.ClearLocked(_bmp, bg);
            var spectrum = frame.Spectrum;
            if (spectrum.Length == 0) return;

            int barCount = Math.Min(spectrum.Length, 48);
            int gap = Math.Max(2, w / (barCount * 26));
            int barWidth = Math.Max(4, (w - gap * (barCount - 1) - 40) / barCount);
            int totalWidth = barCount * barWidth + (barCount - 1) * gap;
            int startX = (w - totalWidth) / 2;

            int baselineY = (int)(h * 0.86);
            int maxH = (int)(h * 0.55);
            _peaks ??= new float[barCount];

            var (baseHue, sat, _) = RendererUtils.RgbToHsl(frame.Accent);

            float bounce = 1f + frame.BassEnergy * 0.25f + (frame.BeatDetected ? 0.08f : 0f);

            for (int i = 0; i < barCount; i++)
            {
                float value = Math.Clamp(spectrum[i], 0f, 1f) * bounce;
                int barH = (int)(value * maxH);
                if (barH < 2) barH = 2;

                int x = startX + i * (barWidth + gap);
                int yTop = baselineY - barH;

                // hue ramps gently across the spectrum → rainbow sheen
                double hue = (baseHue - 90 + i * (180.0 / barCount)) % 360;
                if (hue < 0) hue += 360;

                var cTop = RendererUtils.HslToRgb(hue, sat, 0.95);
                var cMid = RendererUtils.HslToRgb(hue, sat, 0.65);
                var cBase = RendererUtils.HslToRgb(hue, sat * 0.4, 0.18);

                // Warm glow rising off the bar
                RendererUtils.GlowLocked(_bmp, x + barWidth / 2, yTop, barWidth * 1.2f, cTop.R, cTop.G, cTop.B, 0.25f + frame.TrebleEnergy * 0.2f);

                // Bar body: bright tip → dim base
                RendererUtils.FillVerticalGradientLocked(_bmp, x, yTop, barWidth, barH + 2, cTop, cMid);
                // A dim skirt below the baseline for grounding
                RendererUtils.FillVerticalGradientLocked(_bmp, x, baselineY - 2, barWidth, 2, cMid, cBase);

                // Peak-hold line
                if (_peaks[i] == 0) _peaks[i] = barH;
                if (barH > _peaks[i]) _peaks[i] = barH;
                else _peaks[i] = Math.Max(barH, _peaks[i] - 0.9f);

                int peakY = baselineY - (int)_peaks[i];
                var cPeak = RendererUtils.HslToRgb(hue, sat, 1f);
                RendererUtils.FillRectLocked(_bmp, x - 1, peakY - 1, barWidth + 2, 2, cPeak.R, cPeak.G, cPeak.B);
            }

            // Mirror reflection under the baseline
            int reflBottom = Math.Min(h, baselineY + (int)((h - baselineY) * 0.55));
            unsafe
            {
                for (int y0 = baselineY; y0 < reflBottom; y0++)
                {
                    int srcY = baselineY - (y0 - baselineY);
                    if (srcY < 0) break;
                    for (int x = startX; x < startX + totalWidth; x++)
                    {
                        byte* dst = GetPixelPtr(_bmp, x, y0);
                        byte* src = GetPixelPtr(_bmp, x, srcY);
                        float fade = (1f - (float)(y0 - baselineY) / Math.Max(1, reflBottom - baselineY)) * 0.35f;
                        // Reflection fades into the themed backdrop rather than pure black.
                        dst[0] = (byte)(src[0] * fade + bg.B * (1f - fade));
                        dst[1] = (byte)(src[1] * fade + bg.G * (1f - fade));
                        dst[2] = (byte)(src[2] * fade + bg.R * (1f - fade));
                        dst[3] = 255;
                    }
                }
            }
        }
        finally { RendererUtils.FlushLocked(_bmp); }
    }

    private static unsafe byte* GetPixelPtr(WriteableBitmap bmp, int x, int y)
    {
        return (byte*)bmp.BackBuffer + y * bmp.BackBufferStride + x * 4;
    }

    public void Cleanup() { _bmp = null; _peaks = null; }
}